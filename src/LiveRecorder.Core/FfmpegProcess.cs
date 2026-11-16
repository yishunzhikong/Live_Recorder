using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LiveRecorder.Core;

internal static class FfmpegProcess
{
    public static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        IProgress<MediaProgress>? progress,
        CancellationToken cancellationToken,
        Func<Stream, Task>? readOutput = null,
        Func<Stream, CancellationToken, Task>? writeInput = null,
        bool finishOnCancellation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = readOutput is not null,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:2" })
            process.StartInfo.ArgumentList.Add(argument);
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("无法启动内置 FFmpeg，请重新完整解压发布包。", ex);
        }

        using var exitMonitoring = new CancellationTokenSource();
        using var inputWriting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var diagnostics = new Queue<string>();
        Task errors = ReadErrorsAsync(process, diagnostics, progress);
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
                throw new InvalidOperationException($"FFmpeg 退出（{process.ExitCode}）。{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
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

    private static async Task ReadErrorsAsync(Process process, Queue<string> diagnostics, IProgress<MediaProgress>? progress)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            int separator = line.IndexOf('=');
            if (separator > 0 && line[..separator].All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(separator + 1), CultureInfo.InvariantCulture, out long time) &&
                    time >= 0 && time <= TimeSpan.MaxValue.Ticks / 10)
                    progress?.Report(new MediaProgress(MediaTime: TimeSpan.FromMicroseconds(time)));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
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
