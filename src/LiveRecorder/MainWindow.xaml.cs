using System.Diagnostics;
using LiveRecorder.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace LiveRecorder;

/// <summary>调度媒体任务并管理主窗口的进度、结果和关闭流程。</summary>
public sealed partial class MainWindow : Window
{
    private static int _nextLogWindowId;
    // 窗口编号在进程内递增，与 PID 组合后可区分同一日志目录中的事件。
    private readonly int _logWindowId = Interlocked.Increment(ref _nextLogWindowId);
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
            WriteWindowLog(LogLevel.Information, "Window", "窗口已关闭。");
            PortableStorage.Log.FlushAsync().GetAwaiter().GetResult();
        };

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        if (!WindowAspect.Apply(AppWindow, _handle))
            WriteWindowLog(LogLevel.Warning, "Window", "无法启用 16:9 拖动约束，窗口将使用系统调整大小。");
        AppWindow.Closing += Window_Closing;
        WriteWindowLog(LogLevel.Information, "Window", "窗口已打开。");
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
        string output = await _media.DownloadAsync(source, OutputDirectory(), CreateProgress(), token, SelectedWebFormat?.Headers);
        ShowOutput("下载完成", output);
    });

    private async void Record_Click(object sender, RoutedEventArgs e) => await RunAsync("录制中", async token =>
    {
        Uri source = SelectedMediaSource();
        string directory = OutputDirectory();
        MediaSettings settings = PortableStorage.Configuration.Current.Media;
        if (source.Scheme is "rtp" or "udp")
            await ReleaseDatagramPreviewAsync(source);
        WebVideoLink? format = SelectedWebFormat;
        RecordingOptions options = CreateRecordingOptions(settings, _mode == AppMode.Live);
        RecordingResult recording = await _media.RecordAsync(source, directory, options, CreateProgress(), token,
            format?.IsLive, RecordingEvents(token), format?.Headers);
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
            RecordingResult recording = await _media.RecordSdpAsync(file, directory, CreateRecordingOptions(settings, useReconnectSettings: true),
                CreateProgress(), token, RecordingEvents(token));
            await FinishRecordingAsync(recording, directory, settings);
        });
    });

    private static RecordingOptions CreateRecordingOptions(MediaSettings settings, bool useReconnectSettings) =>
        new(settings.PartSizeMb, settings.Encoder, settings.Deinterlace, settings.Encoding, useReconnectSettings ? settings.Reconnect : null);

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

    // 重连事件同步到界面。旧任务的迟到事件只写日志，避免覆盖新任务状态。
    private IProgress<RecordingEvent> RecordingEvents(CancellationToken token) => new Progress<RecordingEvent>(update =>
    {
        string title = update.Recovered ? "录制已恢复" : "等待重连";
        string detail = $"{update.Time:yyyy-MM-dd HH:mm:ss zzz} · {update.Detail}";
        LogLevel level = update.Recovered ? LogLevel.Information : LogLevel.Warning;
        if (_operation?.Token == token && !token.IsCancellationRequested)
            SetStatus(title, detail, level);
        else
            WriteWindowLog(level, "Recording", $"{title} · {detail}");
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
        // 停止录制后仍可合并已保存的分片，因此合并阶段使用新的取消令牌。
        _operation = new CancellationTokenSource();
        CancellationToken token = _operation.Token;
        StartStage("合并中");
        int succeeded = 0;
        int failed = 0;
        var results = new List<string>(folders.Count);
        // 将每次断流之间的录制独立合并，逐目录保留成功、失败和跳过结果。
        foreach (RecordingFolder folder in folders)
        {
            string name = Path.GetFileName(folder.Directory);
            if (token.IsCancellationRequested)
            {
                results.Add($"{name} · 未处理");
                continue;
            }
            SetStatus($"合并中（{results.Count + 1}/{folders.Count}）", name);
            try
            {
                string output = await _media.MergeAsync(folder.Directory, directory, settings.Encoder,
                    choice == ContentDialogResult.Primary, CreateProgress(), token, settings.Deinterlace, settings.Encoding);
                _lastResultFile = output;
                _lastResultFolder = Path.GetDirectoryName(output);
                PlayResultButton.IsEnabled = true;
                succeeded++;
                results.Add($"{name} · 已合并为 {Path.GetFileName(output)}");
                WriteWindowLog(LogLevel.Information, "Recording", $"已合并 {folder.Directory} → {output}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                results.Add($"{name} · 已停止，分片已保留");
            }
            catch (Exception ex)
            {
                failed++;
                results.Add($"{name} · 合并失败，分片已保留。{ex.Message}");
                WriteWindowLog(LogLevel.Error, "Recording", $"合并失败，已保留分片。{folder.Directory}", ex);
            }
        }
        string result = $"已合并 {succeeded}/{folders.Count} 个录制文件夹。";
        if (failed > 0)
            result += $" {failed} 个合并失败，分片已保留。";
        string mergeTitle = token.IsCancellationRequested ? "合并已停止" : failed > 0 ? "合并已结束" : "合并完成";
        SetStatus(mergeTitle, string.Join(Environment.NewLine, new[] { result, directory, "" }.Concat(results)),
            failed > 0 ? LogLevel.Warning : LogLevel.Information);
    }

    /// <summary>在窗口空闲时执行一个前台任务，并统一处理状态反馈。</summary>
    /// <param name="title">任务开始时显示的状态标题。</param>
    /// <param name="action">接收取消令牌的任务回调。</param>
    /// <returns>前台任务及其界面恢复完成的任务。若已有任务在执行，则直接返回。</returns>
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

    // 后台进度先合并到共享状态，再定时刷新界面，避免每次回调都排队更新控件。
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

    // 有活动任务时延后关窗，等待录制分片、对话框和预览资源清理。
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
        WriteWindowLog(level, "Task", $"{title} · {detail}", error);
    }

    private void WriteWindowLog(LogLevel level, string category, string message, Exception? error = null) =>
        PortableStorage.Log.Write(level, category, message, error, _logWindowId);

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
