using System.Runtime.InteropServices;
using System.Text.Json;
using LiveRecorder.Core;
using Windows.Storage;

namespace LiveRecorder;

/// <summary>统一提供工具、缓存、日志和配置的便携存储位置。</summary>
internal static class PortableStorage
{
    /// <summary>便携目录下存放各版本媒体工具的位置。</summary>
    public static string ToolsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "tools");
    /// <summary>未发布媒体、外部工具临时文件和更新资源的缓存位置。</summary>
    public static string CacheDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "cache");
    /// <summary>各应用进程共享的日志目录。</summary>
    public static string LogsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "logs");
    /// <summary>便携配置文件所在的目录。</summary>
    public static string ConfigDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "config");

    /// <summary>获取媒体成品使用的默认目录。</summary>
    /// <returns>当前用户下载目录中的 LiveRecorder 子目录。</returns>
    public static string DefaultOutputDirectory() => Path.Combine(DownloadsDirectory(), "LiveRecorder");
    /// <summary>当前进程共享的便携配置存储。</summary>
    public static PortableConfiguration Configuration { get; } = new(ConfigDirectory);
    /// <summary>当前进程所有窗口共用的日志写入器。</summary>
    public static PortableLog Log { get; } = new(LogsDirectory);
    /// <summary>最近一次初始化中记录的第一个文件访问或配置错误。</summary>
    public static Exception? InitializationError { get; private set; }

    /// <summary>加载便携配置，准备工具、缓存和日志目录，并应用日志档位。</summary>
    public static void Initialize()
    {
        InitializationError = null;
        // 各目录单独初始化，缓存或日志目录故障时仍可载入有效配置。
        TryInitialize(() => Configuration.Load(ToolsDirectory));
        TryInitialize(() => Directory.CreateDirectory(ToolsDirectory));
        TryInitialize(() => Directory.CreateDirectory(CacheDirectory));
        TryInitialize(() => Directory.CreateDirectory(LogsDirectory));
        Log.Configure(Configuration.Current.Logging);
        LoggingSettings logging = Configuration.Current.Logging;
        MediaSettings media = Configuration.Current.Media;
        Log.Write(LogLevel.Debug, "Configuration", $"便携配置已载入 · 日志档位={logging.Level} · 单片={logging.FileSizeMb} MB · 保留={logging.FileCount} · 媒体模式={media.Mode} · 预览解码={media.PreviewDecoder} · 录制分片={media.PartDurationSeconds} 秒。");
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
