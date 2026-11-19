namespace LiveRecorder.Core;

/// <summary>将核心诊断关联到发起任务的窗口，并在关闭 Debug 时跳过输出。</summary>
/// <param name="log">负责过滤和写入诊断日志的便携日志实例。</param>
/// <param name="windowId">日志关联的窗口编号。为空时只关联应用进程。</param>
public sealed class DebugTrace(PortableLog log, int? windowId = null)
{
    /// <summary>指示当前日志配置是否允许写入 Debug 级别。</summary>
    public bool Enabled => log.IsEnabled(LogLevel.Debug);

    /// <summary>写入当前窗口或进程的 Debug 诊断。</summary>
    /// <param name="category">日志所属的诊断类别。</param>
    /// <param name="message">不含原始请求头或凭据的诊断正文。</param>
    public void Write(string category, string message) =>
        log.Write(LogLevel.Debug, category, message, error: null, windowId);
}
