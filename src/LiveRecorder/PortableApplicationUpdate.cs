using System.Diagnostics;
using System.IO.Compression;
using LiveRecorder.Core;

namespace LiveRecorder;

/// <summary>在便携目录准备应用更新，并在当前进程退出后接管安装。</summary>
internal static class PortableApplicationUpdate
{
    private const string Executable = "LiveRecorder.exe";
    private static readonly HashSet<string> Preserved = new(StringComparer.OrdinalIgnoreCase) { "logs", "config", "tools" };

    /// <summary>验证安装路径与下载包，在同一个卷准备完整的新版程序。</summary>
    /// <param name="updater">用于下载和验证发行包的更新服务。</param>
    /// <param name="update">将要安装的 GitHub 稳定发行版。</param>
    /// <param name="progress">接收下载字节数的进度回调。</param>
    /// <param name="token">用于取消下载或解压的令牌。</param>
    /// <returns>等待安装程序接管的新版本暂存目录。</returns>
    /// <exception cref="InvalidDataException">包内路径、文件布局或版本与发行版不符。</exception>
    internal static async Task<string> PrepareAsync(ToolUpdater updater, ApplicationUpdate update,
        IProgress<MediaProgress> progress, CancellationToken token)
    {
        string root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string executable = Path.Combine(root, Executable);
        if (root == Path.GetPathRoot(root) ||
            !string.Equals(Environment.ProcessPath, executable, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("请从独立的便携目录运行 Live Recorder 后再更新。");

        string staging = Directory.CreateDirectory(Path.Combine(root, $".update-{Guid.NewGuid():N}")).FullName;
        try
        {
            PortableStorage.Log.Write(LogLevel.Debug, "Update", $"建立应用更新暂存目录 · 目标版本={update.Tag}。");
            string archive = Path.Combine(staging, "app.zip");
            await updater.DownloadApplicationUpdateAsync(update, archive, progress, token).ConfigureAwait(false);
            string expectedRoot = $"LiveRecorder-{update.Tag}-win-x64";
            string payload = await ExtractAsync(archive, staging, expectedRoot, token).ConfigureAwait(false);
            string newExecutable = Path.Combine(payload, Executable);
            var version = FileVersionInfo.GetVersionInfo(newExecutable);
            if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) != update.Version ||
                !File.Exists(Path.Combine(payload, "LiveRecorder.dll")) ||
                !File.Exists(Path.Combine(payload, "MainWindow.xbf")))
                throw new InvalidDataException("安装包内的程序版本或运行文件与发行版不符。");
            PortableStorage.Log.Write(LogLevel.Debug, "Update", $"应用更新包已解压并通过版本校验 · 程序版本={version.FileVersion}。");
            Directory.Move(payload, Path.Combine(staging, "next"));
            File.Delete(archive);
            await using Stream script = typeof(PortableApplicationUpdate).Assembly
                .GetManifestResourceStream("LiveRecorder.ApplyPortableUpdate.ps1") ??
                throw new InvalidOperationException("无法加载应用更新程序。");
            await using var output = File.Create(Path.Combine(staging, "ApplyPortableUpdate.ps1"));
            await output.WriteAsync(System.Text.Encoding.UTF8.GetPreamble(), token).ConfigureAwait(false);
            await script.CopyToAsync(output, token).ConfigureAwait(false);
            return staging;
        }
        catch
        {
            Discard(staging);
            throw;
        }
    }

    /// <summary>检验便携包的单层包装目录，并在暂存区解压。</summary>
    /// <param name="archive">已验证摘要的 ZIP 文件路径。</param>
    /// <param name="staging">用于解压的隔离暂存目录。</param>
    /// <param name="expectedRoot">以发行标签命名的预期包装目录。</param>
    /// <param name="token">用于取消校验和解压的令牌。</param>
    /// <returns>解压后的便携包根目录。</returns>
    private static async Task<string> ExtractAsync(string archive, string staging, string expectedRoot, CancellationToken token)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string[] parts = entry.FullName.Replace('\\', '/').TrimEnd('/').Split('/');
            if (parts[0] != expectedRoot || parts.Any(part => part.Length == 0 || part is "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                parts.Length > 1 && Preserved.Contains(parts[1]) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("安装包包含不允许替换的目录或无效路径。");
        }
        string unpacked = Directory.CreateDirectory(Path.Combine(staging, "unpacked")).FullName;
        await zip.ExtractToDirectoryAsync(unpacked, cancellationToken: token).ConfigureAwait(false);
        return Path.Combine(unpacked, expectedRoot);
    }

    /// <summary>启动独立更新程序。调用方随后正常退出以释放应用文件。</summary>
    /// <param name="staging">包含已验证新版程序和更新脚本的暂存目录。</param>
    internal static void Start(string staging)
    {
        using Process current = Process.GetCurrentProcess();
        string powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetPathRoot(staging)!
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(staging, "ApplyPortableUpdate.ps1"),
            "-ParentProcessId", current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动应用更新程序。");
        PortableStorage.Log.Write(LogLevel.Debug, "Update", $"已启动独立安装程序 · PID={process.Id}，等待应用退出后替换文件。");
    }

    /// <summary>尝试清理尚未交给安装程序的暂存目录。</summary>
    /// <param name="staging">本次更新创建的暂存目录。</param>
    internal static void Discard(string staging)
    {
        try { Directory.Delete(staging, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PortableStorage.Log.Write(LogLevel.Warning, "Update", "无法清理更新暂存目录。", ex);
        }
    }
}
