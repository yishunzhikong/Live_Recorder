namespace LiveRecorder.Core;

/// <summary>将核心诊断关联到发起任务的窗口，并在关闭 Debug 时跳过输出。</summary>
public sealed class DebugTrace(PortableLog log, int? windowId = null)
{
    public bool Enabled => log.IsEnabled(LogLevel.Debug);

    public void Write(string category, string message) =>
        log.Write(LogLevel.Debug, category, message, error: null, windowId);
}
