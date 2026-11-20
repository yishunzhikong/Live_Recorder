using System.ComponentModel;
using System.Diagnostics;

namespace LiveRecorder.Core;

/// <summary>管理供预览播放器读取的子进程媒体流。</summary>
/// <remarks>调用方应持续消费 <see cref="Content" />，使用结束后异步释放本对象。</remarks>
public sealed class PreviewStream : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenRegistration _cancellation;

    private PreviewStream(Process process, string errorMessage, CancellationToken token, DebugTrace? debug,
        IDisposable? cleanup, bool includeDiagnostics)
    {
        _process = process;
        Content = process.StandardOutput.BaseStream;
        _cancellation = token.UnsafeRegister(static state => ToolProcess.Stop((Process)state!), process);
        Completion = MonitorAsync(process, errorMessage, token, debug, cleanup, includeDiagnostics);
    }

    /// <summary>子进程标准输出中的媒体数据流。</summary>
    public Stream Content { get; }
    /// <summary>监控子进程退出的任务。非零退出通过任务异常报告。</summary>
    public Task Completion { get; }

    /// <summary>启动供流进程并在进程结束后释放它使用的临时资源。</summary>
    /// <param name="start">供流进程启动配置。</param>
    /// <param name="errorMessage">进程退出失败的提示。</param>
    /// <param name="token">取消供流的令牌。</param>
    /// <param name="debug">可选的诊断记录器。</param>
    /// <param name="cleanup">启动成功后由预览流负责释放的临时资源。</param>
    /// <param name="includeDiagnostics">是否在错误提示中附带子进程 stderr。</param>
    /// <returns>读取标准输出媒体数据的预览流。</returns>
    internal static PreviewStream Start(ProcessStartInfo start, string errorMessage, CancellationToken token, DebugTrace? debug = null,
        IDisposable? cleanup = null, bool includeDiagnostics = true)
    {
        token.ThrowIfCancellationRequested();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            debug?.Write("Playback", $"启动预览供流子进程 · 工具={Path.GetFileName(start.FileName)} · PID={process.Id}。");
            return new PreviewStream(process, errorMessage, token, debug, cleanup, includeDiagnostics);
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

    private static async Task MonitorAsync(Process process, string errorMessage, CancellationToken token, DebugTrace? debug,
        IDisposable? cleanup, bool includeDiagnostics)
    {
        using var resources = cleanup;
        // 持续排空 stderr，避免诊断管道填满后阻塞媒体输出。
        Task<string> errors = ToolProcess.ReadDiagnosticTailAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string detail = await errors.ConfigureAwait(false);
                throw new InvalidOperationException(!includeDiagnostics || detail.Length == 0 ? errorMessage : detail);
            }
        }
        finally
        {
            await errors.ConfigureAwait(false);
            if (debug?.Enabled == true)
                debug.Write("Playback", $"预览供流结束 · PID={process.Id} · 状态码={(process.HasExited ? process.ExitCode : (int?)null)} · 已取消={token.IsCancellationRequested}。");
        }
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
