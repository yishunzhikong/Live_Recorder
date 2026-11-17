using System.Runtime.InteropServices;
using System.Text.Json;
using LiveRecorder.Core;
using Windows.Storage;

namespace LiveRecorder;

/// <summary>统一提供工具、缓存、日志和配置的便携存储位置。</summary>
internal static class PortableStorage
{
    public static string ToolsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "tools");
    public static string CacheDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "cache");
    public static string LogsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public static string ConfigDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "config");
    // 成品默认进入当前用户的下载目录。内部工作文件仍位于程序目录。
    public static string DefaultOutputDirectory() => Path.Combine(DownloadsDirectory(), "LiveRecorder");
    public static PortableConfiguration Configuration { get; } = new(ConfigDirectory);
    public static PortableLog Log { get; } = new(LogsDirectory);
    public static Exception? InitializationError { get; private set; }

    public static void Initialize()
    {
        InitializationError = null;
        // 各目录单独初始化，缓存或日志目录故障时仍可载入有效配置。
        TryInitialize(() => Configuration.Load(ToolsDirectory));
        TryInitialize(() => Directory.CreateDirectory(ToolsDirectory));
        TryInitialize(() => Directory.CreateDirectory(CacheDirectory));
        TryInitialize(() => Directory.CreateDirectory(LogsDirectory));
        Log.Configure(Configuration.Current.Logging);
        if (InitializationError is { } error)
            Log.Write(LogLevel.Warning, "Configuration", "无法初始化便携数据目录或读取配置。", error);
        if (Configuration.RecoveryBackup is { } backup)
            Log.Write(LogLevel.Warning, "Configuration", $"配置已恢复。原文件已备份到 {backup}。");

        static void TryInitialize(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                InitializationError ??= ex;
            }
        }
    }

    /// <summary>获取与当前用户匹配的系统下载目录。</summary>
    /// <returns>系统报告的下载路径。无法获取时使用用户目录下的 Downloads 子目录。</returns>
    /// <remarks>优先使用 WinRT 用户路径，必要时回退到 Windows Shell。</remarks>
    public static string DownloadsDirectory()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            UserDataPaths paths = UserDataPaths.GetDefault();
            // GetDefault can name a different user in a multi-user session.
            if (profile.Length > 0 &&
                string.Equals(Path.TrimEndingDirectorySeparator(paths.Profile), Path.TrimEndingDirectorySeparator(profile),
                    StringComparison.OrdinalIgnoreCase) &&
                Path.IsPathFullyQualified(paths.Downloads))
                return paths.Downloads;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // Use the shell lookup when the WinRT paths are unavailable.
        }

        Guid downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        const uint dontVerify = 0x4000;
        int result = SHGetKnownFolderPath(in downloads, dontVerify, nint.Zero, out nint path);
        try
        {
            if (result >= 0 && Marshal.PtrToStringUni(path) is { Length: > 0 } directory)
                return directory;
        }
        finally { Marshal.FreeCoTaskMem(path); }
        return Path.Combine(profile, "Downloads");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);
}
