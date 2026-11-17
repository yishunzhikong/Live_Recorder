using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LiveRecorder.Core;

internal static class ToolProcess
{
    public static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) when (process.HasExited) { }
    }

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
