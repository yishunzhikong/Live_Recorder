using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

public sealed class PortableLog : IDisposable
{
    private static readonly Regex Address = new("""[a-z][a-z0-9+.-]*://[^\s<>"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LogFileName = new(@"\ALiveRecorder-\d{8}-\d{6}-\d{7}\.log\z", RegexOptions.CultureInvariant);
    private const string Prefix = "LiveRecorder-";
    private readonly string _directory;
    private readonly Mutex _mutex;
    private LoggingSettings _settings = new();

    public PortableLog(string directory)
    {
        _directory = directory;
        _mutex = PortableFileMutex.Create(directory);
    }

    public void Configure(LoggingSettings settings)
    {
        settings = settings with { FileSizeMb = Math.Clamp(settings.FileSizeMb, 1, 100), FileCount = Math.Clamp(settings.FileCount, 1, 100) };
        Volatile.Write(ref _settings, settings);
        AccessFiles(() => Trim(ListFiles(), settings.FileCount));
    }

    public void Write(LogLevel level, string category, string message, Exception? error = null)
    {
        LoggingSettings settings = Volatile.Read(ref _settings);
        if (level < settings.Level || level == LogLevel.None)
            return;

        string detail = error is null ? message : $"{message} | {error}";
        detail = Address.Replace(detail, "[URL]");
        detail = detail.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (detail.Length > 8192)
            detail = detail[..8192] + "…";
        string label = level == LogLevel.Information ? "INFO" : level.ToString().ToUpperInvariant();
        var timestamp = DateTimeOffset.Now;
        byte[] line = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{timestamp:O} [{label}] [{category}] {detail}\n"));

        AccessFiles(() =>
        {
            FileInfo[] files = ListFiles();
            FileInfo? current = files.FirstOrDefault();
            if (current is null || current.Length + line.Length > settings.FileSizeMb * 1_000_000L)
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
                string path = Path.Combine(_directory, Prefix + stamp + ".log");
                using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                created.Write(line);
                Trim(files, settings.FileCount - 1);
            }
            else
            {
                using var append = new FileStream(current.FullName, FileMode.Append, FileAccess.Write, FileShare.Read);
                append.Write(line);
                Trim(files, settings.FileCount);
            }
        });
    }

    private FileInfo[] ListFiles() => new DirectoryInfo(_directory).EnumerateFiles(Prefix + "*.log")
        .Where(file => LogFileName.IsMatch(file.Name))
        .OrderByDescending(file => file.Name, StringComparer.Ordinal).ToArray();

    private static void Trim(FileInfo[] files, int keep)
    {
        foreach (FileInfo old in files.Skip(keep))
            old.Delete();
    }

    private void AccessFiles(Action action)
    {
        PortableFileMutex.Enter(_mutex);
        try
        {
            Directory.CreateDirectory(_directory);
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
        finally { _mutex.ReleaseMutex(); }
    }

    public void Dispose() => _mutex.Dispose();
}
