using System.Diagnostics;
using LiveRecorder.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace LiveRecorder;

public sealed partial class MainWindow : Window
{
    private readonly nint _handle;
    private MediaService _media;
    private CancellationTokenSource? _operation;
    private ContentDialog? _mergePrompt;
    private string? _lastResultFolder;
    private DispatcherQueueTimer? _progressTimer;
    private bool _closeWhenIdle;
    private bool _closingAfterCleanup;

    public MainWindow()
    {
        (_media, _webAnalyzer, _ffmpegPath) = ResolveMediaTools();
        InitializeComponent();
        InitializePreviewControls();
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        OutputBox.Text = PortableStorage.DownloadsDirectory();
        StatusDetail.Text = "粘贴媒体链接，或选择要处理的本地文件。";
        try
        {
            Directory.CreateDirectory(PortableStorage.ToolsDirectory);
            Directory.CreateDirectory(PortableStorage.CacheDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("无法创建便携数据目录", $"请确认程序目录可写。{Environment.NewLine}{ex.Message}");
        }
        ModeBox.SelectionChanged += Mode_SelectionChanged;
        SourceBox.TextChanged += (_, _) => UpdateRecordButton();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        WindowAspect.Apply(AppWindow, _handle);
        AppWindow.Closing += Window_Closing;
    }

    private async void Download_Click(object sender, RoutedEventArgs e) => await RunAsync("下载中", async token =>
    {
        if (SelectedWebAudio is { } audio)
        {
            string audioFile = await DownloadWebAudioAsync(audio, OutputDirectory(), token);
            ShowOutput("下载完成", audioFile);
            return;
        }

        Uri source = SelectedMediaSource();
        string output = await _media.DownloadAsync(source, OutputDirectory(), CreateProgress(), token);
        ShowOutput("下载完成", output);
    });

    private async void Record_Click(object sender, RoutedEventArgs e) => await RunAsync("录制中", async token =>
    {
        Uri source = SelectedMediaSource();
        string directory = OutputDirectory();
        VideoEncoder encoder = SelectedEncoder();
        if (source.Scheme is "rtp" or "udp")
            await ReleaseDatagramPreviewAsync(source);
        RecordingResult recording = await _media.RecordAsync(source, directory, PartSize(), encoder,
            CreateProgress(), token, SelectedDeinterlaceMode());
        await FinishRecordingAsync(recording, directory, encoder);
    });

    private async void RecordSdp_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        string? file = await PickFileAsync(".sdp");
        if (file is null)
            return;
        await RunAsync("录制中", async token =>
        {
            string directory = OutputDirectory();
            VideoEncoder encoder = SelectedEncoder();
            await ReleaseDatagramPreviewAsync();
            RecordingResult recording = await _media.RecordSdpAsync(file, directory, PartSize(), encoder,
                CreateProgress(), token, SelectedDeinterlaceMode());
            await FinishRecordingAsync(recording, directory, encoder);
        });
    });

    private async void Merge_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        string? folder = await PickFolderAsync();
        if (folder is null)
            return;
        await RunAsync("合并中", async token =>
        {
            _lastResultFolder = folder;
            string output = await _media.MergeAsync(folder, OutputDirectory(), SelectedEncoder(),
                ReencodeBox.IsChecked != true, CreateProgress(), token, SelectedDeinterlaceMode());
            ShowOutput("合并完成", output);
        });
    });

    private async void Transcode_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        string? file = await PickFileAsync("*");
        if (file is null)
            return;
        await RunAsync("转码中", async token =>
        {
            string output = await _media.TranscodeAsync(file, OutputDirectory(), SelectedEncoder(),
                CreateProgress(), token, SelectedDeinterlaceMode());
            ShowOutput("转码完成", output);
        });
    });

    private async void PickOutput_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        if (await PickFolderAsync() is { } folder)
        {
            OutputBox.Text = folder;
            _lastResultFolder = null;
        }
    });

    private async Task HandlePickerAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { SetStatus("无法完成操作", ex.Message); }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _handle);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async Task<string?> PickFileAsync(string extension)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _handle);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task FinishRecordingAsync(RecordingResult recording, string directory, VideoEncoder encoder)
    {
        _lastResultFolder = recording.Directory;
        string summary = $"已保存 {recording.PartCount} 个分片。{Environment.NewLine}{recording.Directory}";
        if (recording.Interruption is not null)
            summary += $"{Environment.NewLine}{recording.Interruption}";
        string title = recording.Interruption is null ? "录制完成" : "录制中断，已保留分片";
        StopProgressUpdates();
        TaskProgress.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = false;
        SetStatus(title, summary);
        if (_closeWhenIdle)
            return;

        _mergePrompt = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "合并录制分片？",
            Content = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = "原样合并",
            SecondaryButtonText = "重新编码后合并",
            CloseButtonText = "保留分片",
            DefaultButton = ContentDialogButton.Primary
        };
        ContentDialogResult choice;
        try { choice = await _mergePrompt.ShowAsync(); }
        finally { _mergePrompt = null; }
        if (choice == ContentDialogResult.None || _closeWhenIdle)
            return;

        _operation!.Dispose();
        _operation = new CancellationTokenSource();
        StartStage("合并中");
        // The current recording already applied the selected deinterlacing mode.
        string output = await _media.MergeAsync(recording.Directory, directory, encoder,
            choice == ContentDialogResult.Primary, CreateProgress(), _operation.Token, DeinterlaceMode.Off);
        ShowOutput("合并完成", output);
    }

    private async Task RunAsync(string title, Func<CancellationToken, Task> action)
    {
        if (_operation is not null)
            return;
        _operation = new CancellationTokenSource();
        SetControlsEnabled(false);
        StartStage(title);
        try { await action(_operation.Token); }
        catch (OperationCanceledException)
        {
            if (_operation.IsCancellationRequested)
                SetStatus("已停止", "任务已停止。");
            else
                SetStatus("连接超时", "服务器长时间未返回媒体数据。请检查链接和网络连接。");
        }
        catch (Exception ex) { SetStatus("任务失败", ex.Message); }
        finally
        {
            StopProgressUpdates();
            _operation.Dispose();
            _operation = null;
            TaskProgress.Visibility = Visibility.Collapsed;
            StopButton.IsEnabled = false;
            SetControlsEnabled(true);
            CloseWhenReady();
        }
    }

    private void StartStage(string title)
    {
        StopButton.IsEnabled = true;
        TaskProgress.IsIndeterminate = true;
        TaskProgress.Value = 0;
        TaskProgress.Visibility = Visibility.Visible;
        SetStatus(title, "正在读取媒体…");
    }

    private IProgress<MediaProgress> CreateProgress()
    {
        StopProgressUpdates();
        var updates = new PendingMediaProgress();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = true;
        string? size = null;
        string? parts = null;
        string? time = null;
        timer.Tick += (_, _) =>
        {
            if (_progressTimer != timer || _operation?.IsCancellationRequested != false || updates.Take() is not { } progress)
                return;
            if (progress.Bytes is long bytes)
                size = progress.TotalBytes is > 0 ? $"{bytes / 1_000_000d:0.00} / {progress.TotalBytes / 1_000_000d:0.00} MB"
                    : $"{bytes / 1_000_000d:0.00} MB";
            if (progress.TotalBytes is > 0 && progress.Bytes is long received)
            {
                TaskProgress.IsIndeterminate = false;
                TaskProgress.Value = Math.Clamp(received * 100d / progress.TotalBytes.Value, 0, 100);
            }
            if (progress.Parts is int count)
                parts = $"已保存 {count} 片";
            if (progress.MediaTime is TimeSpan duration)
                time = $"媒体时长 {(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            string detail = string.Join("  ·  ", new[] { time, parts, size }.OfType<string>());
            if (StatusDetail.Text != detail)
                StatusDetail.Text = detail;
        };
        _progressTimer = timer;
        timer.Start();
        return updates;
    }

    private void StopProgressUpdates()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
    }

    private sealed class PendingMediaProgress : IProgress<MediaProgress>
    {
        private readonly Lock _gate = new();
        private MediaProgress? _latest;

        public void Report(MediaProgress value)
        {
            lock (_gate)
            {
                _latest = _latest is { } previous
                    ? new MediaProgress(value.Bytes ?? previous.Bytes,
                        value.Bytes is not null ? value.TotalBytes : previous.TotalBytes,
                        value.Parts ?? previous.Parts, value.MediaTime ?? previous.MediaTime)
                    : value;
            }
        }

        public MediaProgress? Take()
        {
            lock (_gate)
            {
                MediaProgress? progress = _latest;
                _latest = null;
                return progress;
            }
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => RequestStop();

    private void RequestStop()
    {
        if (_operation?.IsCancellationRequested != false)
            return;
        StopButton.IsEnabled = false;
        SetStatus("正在停止", "正在关闭媒体连接并处理已写入的数据…");
        _operation.Cancel();
    }

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_operation is null && _toolVersionsDialog is null && _preview is null && _previewCleanup.IsCompleted)
            return;
        args.Cancel = true;
        if (_closeWhenIdle)
            return;
        _closeWhenIdle = true;
        _mergePrompt?.Hide();
        RequestStop();
        Task previewCleanup = StopPreviewAsync();
        _toolVersionsDialog?.CancelAndClose();
        await previewCleanup;
        CloseWhenReady();
    }

    private void CloseWhenReady()
    {
        if (!_closeWhenIdle || _closingAfterCleanup || _operation is not null ||
            _toolVersionsDialog is not null || !_previewCleanup.IsCompleted)
            return;
        _closingAfterCleanup = true;
        Close();
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = _lastResultFolder ?? OutputDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { SetStatus("无法打开文件夹", ex.Message); }
    }

    private void SetControlsEnabled(bool enabled)
    {
        ToolsMenu.IsEnabled = enabled;
        foreach (Control control in new Control[] { ModeBox, WebSourceBox, AnalyzeWebButton,
            SourceBox, PreviewLinkButton, DownloadButton, RecordButton, SdpButton, OutputBox, OutputPickerButton,
            EncoderBox, DeinterlaceBox, PartSizeBox, MergeButton, TranscodeButton, ReencodeBox, OpenOutputButton })
            control.IsEnabled = enabled;
        UpdateWebActions(enabled);
    }

    private void SetStatus(string title, string detail)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
    }

    private void ShowOutput(string title, string file)
    {
        _lastResultFolder = Path.GetDirectoryName(file);
        _lastResultFile = file;
        PlayResultButton.IsEnabled = true;
        SetStatus(title, file);
    }

    private string OutputDirectory()
    {
        string directory = Environment.ExpandEnvironmentVariables(OutputBox.Text.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("请选择有效的输出文件夹。");
        return Path.GetFullPath(directory);
    }

    private int PartSize()
    {
        double size = PartSizeBox.Value;
        if (!double.IsFinite(size) || size != Math.Truncate(size) || size is < 1 or > 1024)
            throw new ArgumentException("请将分片大小设为 1 到 1024 MB 的整数。");
        return (int)size;
    }

    private VideoEncoder SelectedEncoder() => (VideoEncoder)EncoderBox.SelectedIndex;

    private DeinterlaceMode SelectedDeinterlaceMode() => (DeinterlaceMode)DeinterlaceBox.SelectedIndex;
}
