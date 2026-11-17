using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LiveRecorder.Core;

public sealed class PortableLog : IDisposable
{
    private static readonly Regex Address = new("""[a-z][a-z0-9+.-]*://[^\s<>"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LogFileName = new(@"\ALiveRecorder-\d{8}-\d{6}-\d{7}\.log\z", RegexOptions.CultureInvariant);
    private const string Prefix = "LiveRecorder-";
    private readonly string _directory;
    private readonly Mutex _mutex;
    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(2048) { SingleReader = true });
    private readonly Task _worker;
    private LoggingSettings _settings = new();
    private int _dropped;

    private sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message, Exception? Error,
        TaskCompletionSource? Flushed = null);

    public PortableLog(string directory)
    {
        _directory = directory;
        _mutex = PortableFileMutex.Create(directory);
        _worker = Task.Run(WriteQueuedAsync);
    }

    public void Configure(LoggingSettings settings)
    {
        settings = settings with { FileSizeMb = Math.Clamp(settings.FileSizeMb, 1, 100), FileCount = Math.Clamp(settings.FileCount, 1, 100) };
        Volatile.Write(ref _settings, settings);
        AccessFiles(() => Trim(ListFiles(), settings.FileCount));
    }

    public void Write(LogLevel level, string category, string message, Exception? error = null)
    {
        if (level < Volatile.Read(ref _settings).Level || level == LogLevel.None)
            return;
        if (!_queue.Writer.TryWrite(new(DateTimeOffset.Now, level, category, message, error)))
            Interlocked.Increment(ref _dropped);
    }

    public async Task FlushAsync()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new(default, LogLevel.None, "", "", null, finished)).ConfigureAwait(false);
        Task completed = await Task.WhenAny(finished.Task, _worker).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    private async Task WriteQueuedAsync()
    {
        const int batchSize = 128;
        var batch = new List<LogEntry>(batchSize + 1);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            TaskCompletionSource? flushed = null;
            while (batch.Count < batchSize && _queue.Reader.TryRead(out LogEntry? entry))
            {
                flushed = entry.Flushed;
                if (flushed is not null)
                    break;
                batch.Add(entry);
            }
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
                batch.Add(new(DateTimeOffset.Now, LogLevel.Warning, "Logging", $"日志队列已满，跳过 {dropped} 条日志。", null));
            if (batch.Count > 0)
                AccessFiles(() => WriteBatch(batch));
            batch.Clear();
            flushed?.TrySetResult();
        }
    }

    private static byte[] Format(LogEntry entry)
    {
        string detail = entry.Error is null ? entry.Message : $"{entry.Message} | {entry.Error}";
        detail = Address.Replace(detail, "[URL]");
        detail = detail.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (detail.Length > 8192)
            detail = detail[..8192] + "…";
        string label = entry.Level == LogLevel.Information ? "INFO" : entry.Level.ToString().ToUpperInvariant();
        return Encoding.UTF8.GetBytes(FormattableString.Invariant($"{entry.Time:O} [{label}] [{entry.Category}] {detail}\n"));
    }

    private void WriteBatch(List<LogEntry> batch)
    {
        LoggingSettings settings = Volatile.Read(ref _settings);
        List<FileInfo> files = [.. ListFiles()];
        long maximumBytes = settings.FileSizeMb * 1_000_000L;
        FileStream? output = null;
        try
        {
            if (files.Count > 0)
                output = new FileStream(files[0].FullName, FileMode.Append, FileAccess.Write, FileShare.Read);
            foreach (LogEntry entry in batch)
            {
                byte[] line = Format(entry);
                if (output is null || output.Position + line.Length > maximumBytes)
                {
                    output?.Dispose();
                    string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
                    string path = Path.Combine(_directory, Prefix + stamp + ".log");
                    output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    files.Insert(0, new FileInfo(path));
                }
                output.Write(line);
            }
        }
        finally { output?.Dispose(); }
        Trim(files, settings.FileCount);
    }

    private FileInfo[] ListFiles() => new DirectoryInfo(_directory).EnumerateFiles(Prefix + "*.log")
        .Where(file => LogFileName.IsMatch(file.Name))
        .OrderByDescending(file => file.Name, StringComparer.Ordinal).ToArray();

    private static void Trim(IEnumerable<FileInfo> files, int keep)
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

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _worker.GetAwaiter().GetResult(); }
        finally { _mutex.Dispose(); }
    }
}
