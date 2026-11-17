using System.Runtime.InteropServices;

namespace LiveRecorder;

internal static class PortableStorage
{
    public static string ToolsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "tools");
    public static string CacheDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "cache");

    public static string DownloadsDirectory()
    {
        Guid downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        const uint dontVerify = 0x4000;
        int result = SHGetKnownFolderPath(in downloads, dontVerify, nint.Zero, out nint path);
        try
        {
            if (result >= 0 && Marshal.PtrToStringUni(path) is { Length: > 0 } directory)
                return directory;
        }
        finally { Marshal.FreeCoTaskMem(path); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);
}
