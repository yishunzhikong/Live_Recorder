using System.Diagnostics;

namespace LiveRecorder.Core;

internal static class ToolProcess
{
    public static string RequireExecutable(string? path, string name)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"缺少 {name}。请在「工具」菜单中选择「安装 / 升级 {name}」。");
        return path!;
    }

    public static ProcessStartInfo CreateStartInfo(string executable, string cacheDirectory, string? workingDirectory = null)
    {
        string temporary = Directory.CreateDirectory(Path.Combine(cacheDirectory, "temp")).FullName;
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? cacheDirectory
        };
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["TMPDIR"] = temporary;
        start.Environment["DENO_DIR"] = Path.Combine(cacheDirectory, "deno");
        start.Environment["XDG_CACHE_HOME"] = cacheDirectory;
        start.Environment.Remove("FFREPORT");
        return start;
    }
}
