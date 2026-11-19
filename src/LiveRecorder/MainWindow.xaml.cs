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
    private readonly DebugTrace _debug;
    private readonly nint _handle;
    private readonly WindowPlacement _placement;
    private ContentDialog? _activeDialog;
    private bool _closeWhenIdle;
    private bool _closingAfterCleanup;

    public MainWindow()
    {
        _debug = new DebugTrace(PortableStorage.Log, _logWindowId);
        _toolUpdater = new ToolUpdater(PortableStorage.ToolsDirectory, PortableStorage.CacheDirectory,
            configuration: PortableStorage.Configuration, debug: _debug);
        (_webAnalyzer, _ffmpegPath) = ResolveMediaTools();
        InitializeComponent();
        MainLayout.Width = WindowPlacement.LayoutWidth;
        MainLayout.Height = WindowPlacement.LayoutHeight;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        MediaPane.Attach(AppWindow.Id, _handle, _logWindowId, _defaultOutput, ShowDialogAsync);
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
        _placement = new WindowPlacement(AppWindow, _handle);
        _debug.Write("Window", $"初始窗口 · 宽={AppWindow.Size.Width} · 高={AppWindow.Size.Height} · 原生比例约束={_placement.IsResizeConstrained}。");
        if (!_placement.IsResizeConstrained)
            WriteWindowLog(LogLevel.Warning, "Window", "无法启用边框比例缩放，内容仍按原比例显示。");
        AppWindow.Closing += Window_Closing;
        WriteWindowLog(LogLevel.Information, "Window", "窗口已打开。");
    }

    /// <summary>在主布局中显示应用对话框，使其跟随窗口等比例缩放。</summary>
    /// <param name="dialog">需要显示的应用对话框。</param>
    /// <returns>用户选择的对话框结果。</returns>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        // 设置、工具管理和录制结果共享一个对话框层及关闭流程。
        if (_closeWhenIdle || _activeDialog is not null)
            return ContentDialogResult.None;

        dialog.XamlRoot = DialogLayer.XamlRoot;
        _activeDialog = dialog;
        _debug.Write("Window", $"打开应用对话框 · 类型={dialog.GetType().Name}。");
        try
        {
            DialogLayer.Children.Add(dialog);
            return await dialog.ShowAsync(ContentDialogPlacement.InPlace);
        }
        finally
        {
            DialogLayer.Children.Remove(dialog);
            _activeDialog = null;
            _debug.Write("Window", $"关闭应用对话框 · 类型={dialog.GetType().Name}。");
            CloseWhenReady();
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.Downloads };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    // 有活动任务时延后关窗，等待录制分片、对话框和预览资源清理。
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_activeDialog is null && !MediaPane.HasActivity)
            return;
        args.Cancel = true;
        if (_closeWhenIdle)
            return;
        _closeWhenIdle = true;
        Task previewCleanup = MediaPane.BeginCloseAsync();
        if (_activeDialog is ToolVersionsDialog tools)
            tools.CancelAndClose();
        else
            _activeDialog?.Hide();
        await previewCleanup;
        CloseWhenReady();
    }

    private void CloseWhenReady()
    {
        if (!_closeWhenIdle || _closingAfterCleanup || MediaPane.HasActivity || _activeDialog is not null)
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
