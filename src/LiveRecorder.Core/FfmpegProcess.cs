using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LiveRecorder.Core;

internal sealed class FfmpegFailureException(int exitCode, string[] diagnostics) :
    InvalidOperationException($"FFmpeg 退出（{exitCode}）。{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}")
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
}

/// <summary>执行 FFmpeg 并协调输入、输出、诊断和退出清理。</summary>
internal static class FfmpegProcess
{
    /// <summary>运行 FFmpeg 并等待进程及其管道处理完成。</summary>
    /// <param name="executable">FFmpeg 可执行文件路径。</param>
    /// <param name="cacheDirectory">工作目录及工具缓存的根目录。</param>
    /// <param name="arguments">逐项传给 FFmpeg 的参数，不需要预先添加命令行引号。</param>
    /// <param name="progress">可选的媒体时间进度接收方。</param>
    /// <param name="cancellationToken">请求停止本次执行的令牌。</param>
    /// <param name="readOutput">可选的标准输出读取回调。</param>
    /// <param name="writeInput">可选的标准输入写入回调，返回后关闭输入管道。</param>
    /// <param name="finishOnCancellation">取消时是否先请求 FFmpeg 正常退出，让录制输出有机会完成封装。</param>
    /// <param name="videoWritten">可选回调，在进度首次报告已输出视频帧时调用一次。</param>
    /// <param name="readDiagnostic">可选的普通诊断行接收方。</param>
    /// <param name="traceTimestamps">启用包级时间戳诊断，供录制实时码率统计。</param>
    /// <returns>等待进程退出及管道读取完成的任务。</returns>
    /// <exception cref="FfmpegFailureException">FFmpeg 以非零状态退出，且本次执行不属于宽限停止。</exception>
    /// <exception cref="OperationCanceledException">执行前已取消，或运行中取消且未启用宽限停止。</exception>
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
        Action<string>? readDiagnostic = null, bool traceTimestamps = false)
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
        if (traceTimestamps)
            process.StartInfo.ArgumentList.Add("-debug_ts");
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
        Task exit = Task.CompletedTask, errors = Task.CompletedTask, output = Task.CompletedTask,
            input = Task.CompletedTask, stopping = Task.CompletedTask;

        try
        {
            exit = process.WaitForExitAsync();
            errors = ReadErrorsAsync(process, diagnostics, progress, videoWritten, readDiagnostic, traceTimestamps);
            stopping = StopOnCancellationAsync(process, finishOnCancellation, cancellationToken, exitMonitoring.Token);
            if (readOutput is not null)
                output = readOutput(process.StandardOutput.BaseStream);
            if (writeInput is not null)
                input = WriteInputAsync(process.StandardInput.BaseStream, writeInput, inputWriting.Token);

            // A failed disk write must stop FFmpeg even if its stdout pipe is still full.
            await foreach (Task completed in Task.WhenEach(exit, output, errors).ConfigureAwait(false))
                await completed.ConfigureAwait(false);

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
            ToolProcess.Stop(process);
            try { await Task.WhenAll(exit, input, output, errors, stopping).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original failure. */ }
        }
    }

    private static async Task WriteInputAsync(Stream stream, Func<Stream, CancellationToken, Task> writeInput,
        CancellationToken cancellationToken)
    {
        // 写完后关闭标准输入，让读取 pipe:0 的 FFmpeg 收到 EOF。
        await using (stream)
            await writeInput(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadErrorsAsync(Process process, Queue<string> diagnostics, IProgress<MediaProgress>? progress,
        Action? videoWritten, Action<string>? readDiagnostic, bool traceTimestamps)
    {
        // 启用进度后，stderr 同时承载键值进度和普通诊断。
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
                if (traceTimestamps && IsTimestampTrace(line))
                    continue;
                if (diagnostics.Count == 10)
                    diagnostics.Dequeue();
                diagnostics.Enqueue(line);
            }
        }
    }

    // 包级诊断只用于测量。保留错误队列给真正的失败原因。
    private static bool IsTimestampTrace(string line) =>
        line.Contains(" pkt_pts:", StringComparison.Ordinal) || line.Contains(" frame_pts:", StringComparison.Ordinal) ||
        line.Contains("muxer <- ", StringComparison.Ordinal) || line.Contains("encoder <- ", StringComparison.Ordinal) ||
        line.Contains("encoder -> ", StringComparison.Ordinal) || line.Contains("filter -> ", StringComparison.Ordinal);

    private static async Task StopOnCancellationAsync(Process process, bool graceful,
        CancellationToken cancellationToken, CancellationToken exitMonitoring)
    {
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, exitMonitoring);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, combined.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        if (exitMonitoring.IsCancellationRequested)
            return;

        // 录制停止时给 FFmpeg 留出写完容器尾部的机会，超时后再终止进程树。
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
        ToolProcess.Stop(process);
    }
}
