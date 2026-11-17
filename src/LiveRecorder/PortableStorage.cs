using System.Runtime.InteropServices;
using System.Text.Json;
using LiveRecorder.Core;
using Windows.Storage;

namespace LiveRecorder;

internal static class PortableStorage
{
    public static string ToolsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "tools");
    public static string CacheDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "cache");
    public static string LogsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public static string ConfigDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "config");
    public static string DefaultOutputDirectory() => Path.Combine(DownloadsDirectory(), "LiveRecorder");
    public static PortableConfiguration Configuration { get; } = new(ConfigDirectory);
    public static PortableLog Log { get; } = new(LogsDirectory);
    public static Exception? InitializationError { get; private set; }

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(ToolsDirectory);
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Configuration.Load(ToolsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            InitializationError = ex;
        }
        Log.Configure(Configuration.Current.Logging);
        if (InitializationError is { } error)
            Log.Write(LogLevel.Warning, "Configuration", "无法初始化便携数据目录或读取配置。", error);
        if (Configuration.RecoveryBackup is { } backup)
            Log.Write(LogLevel.Warning, "Configuration", $"配置已恢复。原文件已备份到 {backup}。");
    }

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
