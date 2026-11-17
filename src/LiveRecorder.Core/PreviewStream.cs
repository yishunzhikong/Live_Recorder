using System.ComponentModel;
using System.Diagnostics;

namespace LiveRecorder.Core;

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

    public Stream Content { get; }
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

    public async ValueTask DisposeAsync()
    {
        try
        {
            ToolProcess.Stop(_process);
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
