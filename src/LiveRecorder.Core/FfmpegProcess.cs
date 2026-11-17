using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LiveRecorder.Core;

internal sealed class FfmpegFailureException(int exitCode, string[] diagnostics) :
    InvalidOperationException($"FFmpeg 退出（{exitCode}）。{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}")
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
}

internal static class FfmpegProcess
{
    public static async Task RunAsync(
        string? executable,
        string cacheDirectory,
        IEnumerable<string> arguments,
        IProgress<MediaProgress>? progress,
        CancellationToken cancellationToken,
        Func<Stream, Task>? readOutput = null,
        Func<Stream, CancellationToken, Task>? writeInput = null,
        bool finishOnCancellation = false,
        Action? videoWritten = null,
        Action<string>? readDiagnostic = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo start = ToolProcess.CreateStartInfo(ToolProcess.RequireExecutable(executable, "FFmpeg"), cacheDirectory);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = readOutput is not null;
        start.RedirectStandardError = true;
        start.StandardErrorEncoding = Encoding.UTF8;
        using var process = new Process { StartInfo = start };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "warning", "-nostats" })
            process.StartInfo.ArgumentList.Add(argument);
        if (progress is not null || videoWritten is not null)
        {
            process.StartInfo.ArgumentList.Add("-progress");
            process.StartInfo.ArgumentList.Add("pipe:2");
        }
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("无法启动 FFmpeg，请在「工具」菜单中检查安装。", ex);
        }

        using var exitMonitoring = new CancellationTokenSource();
        using var inputWriting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var diagnostics = new Queue<string>();
        Task errors = ReadErrorsAsync(process, diagnostics, progress, videoWritten, readDiagnostic);
        Task exit = process.WaitForExitAsync();
        Task output = readOutput?.Invoke(process.StandardOutput.BaseStream) ?? Task.CompletedTask;
        Task input = writeInput is null
            ? Task.CompletedTask
            : WriteInputAsync(process.StandardInput.BaseStream, writeInput, inputWriting.Token);
        Task stopping = StopOnCancellationAsync(process, finishOnCancellation, cancellationToken, exitMonitoring.Token);

        try
        {
            // A failed disk write must stop FFmpeg even if its stdout pipe is still full.
            var running = new List<Task> { exit, output, errors };
            while (running.Count != 0)
            {
                Task completed = await Task.WhenAny(running).ConfigureAwait(false);
                running.Remove(completed);
                await completed.ConfigureAwait(false);
            }

            if (!finishOnCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 && !(finishOnCancellation && cancellationToken.IsCancellationRequested))
                throw new FfmpegFailureException(process.ExitCode, diagnostics.ToArray());
            await input.ConfigureAwait(false);
        }
        finally
        {
            exitMonitoring.Cancel();
            inputWriting.Cancel();
            Kill(process);
            try { await Task.WhenAll(exit, input, output, errors, stopping).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original failure. */ }
        }
    }

    private static async Task WriteInputAsync(Stream stream, Func<Stream, CancellationToken, Task> writeInput,
        CancellationToken cancellationToken)
    {
        await using (stream)
            await writeInput(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadErrorsAsync(Process process, Queue<string> diagnostics, IProgress<MediaProgress>? progress,
        Action? videoWritten, Action<string>? readDiagnostic)
    {
        bool readProgress = progress is not null || videoWritten is not null;
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            int separator = readProgress ? line.IndexOf('=') : -1;
            if (separator > 0 && line[..separator].All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(separator + 1), CultureInfo.InvariantCulture, out long time) &&
                    time >= 0 && time <= TimeSpan.MaxValue.Ticks / 10)
                    progress?.Report(new MediaProgress(MediaTime: TimeSpan.FromMicroseconds(time)));
                if (videoWritten is not null && line.StartsWith("frame=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(separator + 1), CultureInfo.InvariantCulture, out long frames) && frames > 0)
                {
                    videoWritten();
                    videoWritten = null;
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                readDiagnostic?.Invoke(line);
                if (diagnostics.Count == 10)
                    diagnostics.Dequeue();
                diagnostics.Enqueue(line);
            }
        }
    }

    private static async Task StopOnCancellationAsync(Process process, bool graceful,
        CancellationToken cancellationToken, CancellationToken exitMonitoring)
    {
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, exitMonitoring);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, combined.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        if (exitMonitoring.IsCancellationRequested)
            return;

        if (graceful)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(exitMonitoring);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await process.StandardInput.WriteLineAsync("q".AsMemory(), deadline.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
        }
        Kill(process);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
}
