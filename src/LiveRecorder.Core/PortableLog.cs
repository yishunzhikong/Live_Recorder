using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace LiveRecorder.Core;

/// <summary>将日志排队并批量写入共享的便携目录。</summary>
/// <remarks>当前进程中的窗口共用队列。多个实例按目录获取同一命名互斥锁，协调写入和轮转。</remarks>
public sealed class PortableLog : IDisposable
{
    private static readonly Regex Address = new("""[a-z][a-z0-9+.-]*://[^\s<>"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LogFileName = new(@"\ALiveRecorder-\d{8}-\d{6}-\d{7}\.log\z", RegexOptions.CultureInvariant);
    private static readonly string ProcessScope = FormattableString.Invariant($"[PID={Environment.ProcessId}]");
    private const string Prefix = "LiveRecorder-";
    private readonly string _directory;
    private readonly Mutex _mutex;
    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(2048) { SingleReader = true });
    private readonly Task _worker;
    private LoggingSettings _settings = new();
    private int _dropped;

    private sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message, Exception? Error,
        int? WindowId = null, TaskCompletionSource? Flushed = null);

    /// <summary>启动便携日志的后台写入器。</summary>
    /// <param name="directory">各实例共用的日志目录。</param>
    public PortableLog(string directory)
    {
        _directory = directory;
        _mutex = PortableFileMutex.Create(directory);
        _worker = Task.Run(WriteQueuedAsync);
    }

    /// <summary>应用本实例的级别和轮转设置，并按保留数量清理现有文件。</summary>
    /// <param name="settings">新的日志设置。文件大小与文件数量会限制在 1 至 100。</param>
    public void Configure(LoggingSettings settings)
    {
        settings = settings with { FileSizeMb = Math.Clamp(settings.FileSizeMb, 1, 100), FileCount = Math.Clamp(settings.FileCount, 1, 100) };
        Volatile.Write(ref _settings, settings);
        AccessFiles(() => Trim(ListFiles(), settings.FileCount));
    }

    /// <summary>提交一条进程级日志。</summary>
    /// <param name="level">日志级别。</param>
    /// <param name="category">用于归类的类别名。</param>
    /// <param name="message">日志正文。</param>
    /// <param name="error">可选的关联异常。</param>
    /// <remarks>日志会按级别过滤。队列满时跳过本条，由后台写入器汇报跳过条数。</remarks>
    public void Write(LogLevel level, string category, string message, Exception? error = null) =>
        Write(level, category, message, error, windowId: null);

    /// <summary>提交一条可关联到具体窗口的日志。</summary>
    /// <param name="level">日志级别。</param>
    /// <param name="category">用于归类的类别名。</param>
    /// <param name="message">日志正文。</param>
    /// <param name="error">可选的关联异常。</param>
    /// <param name="windowId">进程内窗口编号。为空时只记录进程 ID。</param>
    public void Write(LogLevel level, string category, string message, Exception? error, int? windowId)
    {
        if (level < Volatile.Read(ref _settings).Level || level == LogLevel.None)
            return;
        // 入队时记录时间和窗口编号。队列满时记下积压数量，避免阻塞界面线程。
        if (!_queue.Writer.TryWrite(new(DateTimeOffset.Now, level, category, message, error, windowId)))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>等待队列中本次刷新标记之前的日志处理完成。</summary>
    /// <returns>刷新完成的任务。</returns>
    /// <remarks>文件写入错误由写入器处理，刷新不代表每条日志都已成功落盘。</remarks>
    public async Task FlushAsync()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new(default, LogLevel.None, "", "", null, Flushed: finished)).ConfigureAwait(false);
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
        string windowScope = entry.WindowId is int windowId ? FormattableString.Invariant($" [Window={windowId}]") : "";
        return Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{entry.Time:O} {ProcessScope}{windowScope} [{label}] [{entry.Category}] {detail}\n"));
    }

    // 在一次持锁期间写入整批日志，按单文件大小和共享目录的保留数量轮转。
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

    /// <summary>关闭日志队列，等待处理剩余日志并释放互斥锁。</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _worker.GetAwaiter().GetResult(); }
        finally { _mutex.Dispose(); }
    }
}
