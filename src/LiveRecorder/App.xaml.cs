using LiveRecorder.Core;
using Microsoft.UI.Xaml;

namespace LiveRecorder;

public partial class App : Application
{
    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        PortableStorage.Initialize();
        UnhandledException += (_, error) =>
            PortableStorage.Log.Write(LogLevel.Error, "Application", "未处理的应用异常。", error.Exception);
        var version = typeof(App).Assembly.GetName().Version;
        PortableStorage.Log.Write(LogLevel.Information, "Application", $"Live Recorder {version} 已启动。");
        _window = new MainWindow();
        _window.Activate();
    }
}
