using System.Diagnostics;
using LiveRecorder.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

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
        StatusDetail.Text = "粘贴媒体链接，或选择要处理的本地文件。";
        ModeBox.SelectionChanged += Mode_SelectionChanged;
        RestoreMediaSettings();
        if (PortableStorage.InitializationError is { } error)
            SetStatus("无法载入便携配置", error.Message, LogLevel.Warning);
        else if (PortableStorage.Configuration.RecoveryBackup is { } backup)
            SetStatus("配置已恢复", $"原文件已备份到 {backup}。", LogLevel.Warning);
        Closed += (_, _) =>
        {
            SaveMediaSettings();
            PortableStorage.Log.Write(LogLevel.Information, "Application", "Live Recorder 已退出。");
        };

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
        MediaSettings settings = PortableStorage.Configuration.Current.Media;
        if (source.Scheme is "rtp" or "udp")
            await ReleaseDatagramPreviewAsync(source);
        RecordingResult recording = await _media.RecordAsync(source, directory, settings.PartSizeMb, settings.Encoder,
            CreateProgress(), token, settings.Deinterlace, settings.Encoding, SelectedWebFormat?.IsLive, RecordingEvents(token),
            _mode == AppMode.Live ? settings.Reconnect : null);
        await FinishRecordingAsync(recording, directory, settings);
    });

    private async void RecordSdp_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        string? file = await PickFileAsync(".sdp");
        if (file is null)
            return;
        await RunAsync("录制中", async token =>
        {
            string directory = OutputDirectory();
            MediaSettings settings = PortableStorage.Configuration.Current.Media;
            await ReleaseDatagramPreviewAsync();
            RecordingResult recording = await _media.RecordSdpAsync(file, directory, settings.PartSizeMb, settings.Encoder,
                CreateProgress(), token, settings.Deinterlace, settings.Encoding, RecordingEvents(token), settings.Reconnect);
            await FinishRecordingAsync(recording, directory, settings);
        });
    });

    private async void Merge_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        string? folder = NativeOutputPicker.Pick(_handle, _defaultOutput, "选择录制分片文件夹", pickFolder: true);
        if (folder is null)
            return;
        await RunAsync("合并中", async token =>
        {
            _lastResultFolder = folder;
            MediaSettings settings = PortableStorage.Configuration.Current.Media;
            string output = await _media.MergeAsync(folder, OutputDirectory(), settings.Encoder,
                !settings.Reencode, CreateProgress(), token, settings.Deinterlace, settings.Encoding);
            ShowOutput("合并完成", output);
        });
    });

    private async void Transcode_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        string? file = NativeOutputPicker.Pick(_handle, _defaultOutput, "选择要转码的媒体", pickFolder: false);
        if (file is null)
            return;
        await RunAsync("转码中", async token =>
        {
            MediaSettings settings = PortableStorage.Configuration.Current.Media;
            string output = await _media.TranscodeAsync(file, OutputDirectory(), settings.Encoder,
                CreateProgress(), token, settings.Deinterlace, settings.Encoding);
            ShowOutput("转码完成", output);
        });
    });

    private async Task HandlePickerAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { SetStatus("无法完成操作", ex.Message, LogLevel.Error, ex); }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.Downloads };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async Task<string?> PickFileAsync(string extension)
    {
        var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        if (extension != "*")
            picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private IProgress<RecordingEvent> RecordingEvents(CancellationToken token) => new Progress<RecordingEvent>(update =>
    {
        string title = update.Recovered ? "录制已恢复" : "等待重连";
        string detail = $"{update.Time:yyyy-MM-dd HH:mm:ss zzz} · {update.Detail}";
        LogLevel level = update.Recovered ? LogLevel.Information : LogLevel.Warning;
        if (_operation?.Token == token && !token.IsCancellationRequested)
            SetStatus(title, detail, level);
        else
            PortableStorage.Log.Write(level, "Recording", $"{title} · {detail}");
    });

    private async Task FinishRecordingAsync(RecordingResult recording, string directory, MediaSettings settings)
    {
        IReadOnlyList<RecordingFolder> folders = recording.Folders;
        _lastResultFolder = folders.Count == 1 ? folders[0].Directory : directory;
        string summary = $"已保存 {folders.Count} 个录制文件夹，共 {folders.Sum(folder => folder.PartCount)} 个分片。";
        if (folders.Any(folder => folder.Reencoded))
            summary += $"{Environment.NewLine}部分原流无法直接封装为录制分片，已按编码设置录制。";
        if (recording.Interruption is not null)
            summary += $"{Environment.NewLine}{recording.Interruption}";
        string title = recording.Interruption is null ? "录制完成" : "录制中断，已保留分片";
        StopProgressUpdates();
        TaskProgress.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = false;
        SetStatus(title, $"{summary}{Environment.NewLine}{_lastResultFolder}",
            recording.Interruption is null ? LogLevel.Information : LogLevel.Warning);
        if (_closeWhenIdle)
            return;

        _mergePrompt = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "合并录制分片？",
            Content = new ScrollViewer
            {
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, new[] { summary, "" }.Concat(folders.Select(folder =>
                        $"{Path.GetFileName(folder.Directory)} · {folder.PartCount} 片"))),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
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
        CancellationToken token = _operation.Token;
        StartStage("合并中");
        int succeeded = 0;
        int failed = 0;
        foreach (RecordingFolder folder in folders)
        {
            token.ThrowIfCancellationRequested();
            SetStatus($"合并中（{succeeded + failed + 1}/{folders.Count}）", Path.GetFileName(folder.Directory));
            try
            {
                string output = await _media.MergeAsync(folder.Directory, directory, settings.Encoder,
                    choice == ContentDialogResult.Primary, CreateProgress(), token, settings.Deinterlace, settings.Encoding);
                _lastResultFile = output;
                _lastResultFolder = Path.GetDirectoryName(output);
                PlayResultButton.IsEnabled = true;
                succeeded++;
                PortableStorage.Log.Write(LogLevel.Information, "Recording", $"已合并 {folder.Directory} → {output}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                failed++;
                PortableStorage.Log.Write(LogLevel.Error, "Recording", $"合并失败，已保留分片。{folder.Directory}", ex);
            }
        }
        string result = $"已合并 {succeeded}/{folders.Count} 个录制文件夹。";
        if (failed > 0)
            result += $" {failed} 个合并失败，分片已保留，详情见日志。";
        SetStatus(failed > 0 ? "合并已结束" : "合并完成", $"{result}{Environment.NewLine}{directory}",
            failed > 0 ? LogLevel.Warning : LogLevel.Information);
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
                SetStatus("连接超时", "服务器长时间未返回媒体数据。请检查链接和网络连接。", LogLevel.Warning);
        }
        catch (Exception ex) { SetStatus("任务失败", ex.Message, LogLevel.Error, ex); }
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
        catch (Exception ex) { SetStatus("无法打开文件夹", ex.Message, LogLevel.Error, ex); }
    }

    private void SetControlsEnabled(bool enabled)
    {
        ToolsMenu.IsEnabled = enabled;
        foreach (Control control in new Control[] { ModeBox, WebSourceBox, AnalyzeWebButton,
            SourceBox, RecordingStallBox, RecordingRetryBox, RecordingWaitBox,
            PreviewLinkButton, DownloadButton, RecordButton, SdpButton, MergeButton, TranscodeButton, OpenOutputButton })
            control.IsEnabled = enabled;
        UpdateWebActions(enabled);
    }

    private void SetStatus(string title, string detail, LogLevel level = LogLevel.Information, Exception? error = null)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        PortableStorage.Log.Write(level, "Task", $"{title} · {detail}", error);
    }

    private void ShowOutput(string title, string file)
    {
        _lastResultFolder = Path.GetDirectoryName(file);
        _lastResultFile = file;
        PlayResultButton.IsEnabled = true;
        SetStatus(title, file);
    }

    private string OutputDirectory() =>
        NormalizeOutputDirectory(PortableStorage.Configuration.Current.Media.OutputDirectory ?? _defaultOutput);

    private static string NormalizeOutputDirectory(string value)
    {
        string directory = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("请选择有效的输出文件夹。");
        return Path.GetFullPath(directory);
    }

}
