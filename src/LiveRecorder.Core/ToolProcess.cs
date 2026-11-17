using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LiveRecorder.Core;

/// <summary>提供媒体子进程的启动环境、诊断读取和停止操作。</summary>
internal static class ToolProcess
{
    public static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) when (process.HasExited) { }
    }

    /// <summary>持续排空诊断流并保留有限长度的末尾文本。</summary>
    /// <param name="reader">进程诊断输出的读取器。</param>
    /// <returns>最多保留 1800 个字符并去除首尾空白的诊断文本。</returns>
    public static async Task<string> ReadDiagnosticTailAsync(StreamReader reader)
    {
        const int maxCharacters = 1800;
        var tail = new StringBuilder(maxCharacters);
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            int discard = tail.Length + count - maxCharacters;
            if (discard > 0)
                tail.Remove(0, discard);
            tail.Append(buffer, 0, count);
        }
        return tail.ToString().Trim();
    }

    public static string RequireExecutable(string? path, string name)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"缺少 {name}。请在「工具」菜单中选择「安装 / 升级 {name}」。");
        return path!;
    }

    /// <summary>构建无控制台窗口的工具启动配置，并重定向临时文件及缓存路径。</summary>
    /// <param name="executable">工具可执行文件路径。</param>
    /// <param name="cacheDirectory">便携缓存根目录。</param>
    /// <param name="workingDirectory">可选的工作目录，默认使用便携缓存。</param>
    /// <returns>尚未启动的进程配置。</returns>
    public static ProcessStartInfo CreateStartInfo(string executable, string cacheDirectory, string? workingDirectory = null)
    {
        string temporary = Directory.CreateDirectory(Path.Combine(cacheDirectory, "temp")).FullName;
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? cacheDirectory
        };
        // 把工具的临时文件和缓存留在便携程序目录内。
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["TMPDIR"] = temporary;
        start.Environment["DENO_DIR"] = Path.Combine(cacheDirectory, "deno");
        start.Environment["XDG_CACHE_HOME"] = cacheDirectory;
        start.Environment.Remove("FFREPORT");
        return start;
    }
}
