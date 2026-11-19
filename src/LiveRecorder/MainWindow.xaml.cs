using LiveRecorder.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace LiveRecorder;

/// <summary>管理左栏、菜单及主窗口生命周期，并连接独立的媒体操作面板。</summary>
public sealed partial class MainWindow : Window
{
    private static int _nextLogWindowId;
    // 窗口编号在进程内递增，与 PID 组合后可区分同一日志目录中的事件。
    private readonly int _logWindowId = Interlocked.Increment(ref _nextLogWindowId);
    private readonly nint _handle;
    private bool _closeWhenIdle;
    private bool _closingAfterCleanup;

    public MainWindow()
    {
        (_webAnalyzer, _ffmpegPath) = ResolveMediaTools();
        InitializeComponent();
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        MediaPane.Attach(AppWindow.Id, _handle, _logWindowId, PortableStorage.DefaultOutputDirectory());
        MediaPane.SetTools(_ffmpegPath, _webAnalyzer);
        MediaPane.VisibleHeightChanged += height => SettingsCard.Height = height;
        MediaPane.BusyChanged += (_, _) =>
        {
            SetControlsEnabled(!MediaPane.IsBusy);
            CloseWhenReady();
        };
        SourceBox.TextChanged += (_, _) => PublishMediaSource();
        PublishMediaSource();
        MediaPane.SetStatus("就绪", "粘贴媒体链接，或选择要处理的本地文件。", LogLevel.Debug);
        ModeBox.SelectionChanged += Mode_SelectionChanged;
        RestoreMediaSettings();
        if (PortableStorage.InitializationError is { } error)
            MediaPane.SetStatus("无法载入便携配置", error.Message, LogLevel.Warning);
        else if (PortableStorage.Configuration.RecoveryBackup is { } backup)
            MediaPane.SetStatus("配置已恢复", $"原文件已备份到 {backup}。", LogLevel.Warning);
        Closed += (_, _) =>
        {
            SaveMediaSettings();
            MediaPane.DisposePlayer();
            WriteWindowLog(LogLevel.Information, "Window", "窗口已关闭。");
            PortableStorage.Log.FlushAsync().GetAwaiter().GetResult();
        };

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        WindowPlacement.Apply(AppWindow, _handle);
        AppWindow.Closing += Window_Closing;
        WriteWindowLog(LogLevel.Information, "Window", "窗口已打开。");
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.Downloads };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    // 有活动任务时延后关窗，等待录制分片、对话框和预览资源清理。
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_toolVersionsDialog is null && !MediaPane.HasActivity)
            return;
        args.Cancel = true;
        if (_closeWhenIdle)
            return;
        _closeWhenIdle = true;
        Task previewCleanup = MediaPane.BeginCloseAsync();
        _toolVersionsDialog?.CancelAndClose();
        await previewCleanup;
        CloseWhenReady();
    }

    private void CloseWhenReady()
    {
        if (!_closeWhenIdle || _closingAfterCleanup || MediaPane.HasActivity || _toolVersionsDialog is not null)
            return;
        _closingAfterCleanup = true;
        Close();
    }

    private void SetControlsEnabled(bool enabled)
    {
        ToolsMenu.IsEnabled = enabled;
        foreach (Control control in new Control[] { ModeBox, WebSourceBox, AnalyzeWebButton, SourceBox })
            control.IsEnabled = enabled;
        UpdateWebActions(enabled);
    }

    private void WriteWindowLog(LogLevel level, string category, string message, Exception? error = null) =>
        PortableStorage.Log.Write(level, category, message, error, _logWindowId);
}
