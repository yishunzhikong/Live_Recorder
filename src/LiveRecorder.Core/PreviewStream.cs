using System.ComponentModel;
using System.Diagnostics;

namespace LiveRecorder.Core;

/// <summary>管理供预览播放器读取的子进程媒体流。</summary>
/// <remarks>调用方应持续消费 <see cref="Content" />，使用结束后异步释放本对象。</remarks>
public sealed class PreviewStream : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenRegistration _cancellation;

    private PreviewStream(Process process, string errorMessage, CancellationToken token)
    {
        _process = process;
        Content = process.StandardOutput.BaseStream;
        _cancellation = token.UnsafeRegister(static state => ToolProcess.Stop((Process)state!), process);
        Completion = MonitorAsync(process, errorMessage, token);
    }

    /// <summary>子进程标准输出中的媒体数据流。</summary>
    public Stream Content { get; }
    /// <summary>监控子进程退出的任务。非零退出通过任务异常报告。</summary>
    public Task Completion { get; }

    internal static PreviewStream Start(ProcessStartInfo start, string errorMessage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            return new PreviewStream(process, errorMessage, token);
        }
        catch (Exception ex)
        {
            ToolProcess.Stop(process);
            process.Dispose();
            if (ex is Win32Exception)
                throw new InvalidOperationException(errorMessage, ex);
            throw;
        }
    }

    private static async Task MonitorAsync(Process process, string errorMessage, CancellationToken token)
    {
        // 持续排空 stderr，避免诊断管道填满后阻塞媒体输出。
        Task<string> errors = ToolProcess.ReadDiagnosticTailAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string detail = await errors.ConfigureAwait(false);
                throw new InvalidOperationException(detail.Length == 0 ? errorMessage : detail);
            }
        }
        finally { await errors.ConfigureAwait(false); }
    }

    /// <summary>停止子进程并释放诊断监控、取消注册和输出流。</summary>
    /// <returns>等待清理完成的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            ToolProcess.Stop(_process);
            // 等监控结束后再释放管道和进程，避免读取任务访问已释放的句柄。
            await Completion.ConfigureAwait(false);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            try
            {
                await _cancellation.DisposeAsync().ConfigureAwait(false);
                await Content.DisposeAsync().ConfigureAwait(false);
            }
            finally { _process.Dispose(); }
        }
    }
}
