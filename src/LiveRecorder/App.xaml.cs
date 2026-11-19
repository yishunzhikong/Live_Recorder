using LiveRecorder.Core;
using Microsoft.UI.Xaml;

namespace LiveRecorder;

/// <summary>负责 WinUI 应用的进程级初始化和主窗口启动。</summary>
public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>初始化应用级 WinUI 资源。</summary>
    public App() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 窗口恢复设置和解析工具路径前，先载入同目录的便携配置。
        PortableStorage.Initialize();
        UnhandledException += (_, error) =>
            PortableStorage.Log.Write(LogLevel.Error, "Application", "未处理的应用异常。", error.Exception);
        var version = typeof(App).Assembly.GetName().Version;
        PortableStorage.Log.Write(LogLevel.Information, "Application", $"Live Recorder {version} 已启动。");
        _window = new MainWindow();
        _window.Activate();
    }
}
