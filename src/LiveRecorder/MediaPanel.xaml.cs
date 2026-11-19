using System.Diagnostics;
using LiveRecorder.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace LiveRecorder;

/// <summary>承接左栏提交的媒体源，管理右栏播放、下载、录制、本地处理和任务状态。</summary>
public sealed partial class MediaPanel : UserControl
{
    /// <summary>左栏选中的直链及该格式的可选请求信息。</summary>
    /// <param name="Source">已校验的媒体直链。</param>
    /// <param name="WebFormat">解析得到的音视频、直播和请求头信息。手动输入直链时可为空。</param>
    internal sealed record Input(Uri Source, WebVideoLink? WebFormat);

    private readonly SemaphoreSlim _sourceLock = new(1);
    private Input? _input;
    private MediaService.NetworkMediaSelection? _selectedNetwork;
    private string _sourceError = "";
    private string PreviewIdleMessage { get; set; } = PreviewIdle;
    private MediaService _media = null!;
    private WebVideoAnalyzer _webAnalyzer = null!;
    private string? _ffmpegPath;
    private WindowId _windowId;
    private nint _handle;
    private int _logWindowId;
    private DebugTrace? _debug;
    private Func<ContentDialog, Task<ContentDialogResult>> _showDialogAsync = null!;
    private string _defaultOutput = "";
    private CancellationTokenSource? _operation;
    private string? _lastResultFolder;
    private DispatcherQueueTimer? _progressTimer;
    private bool _waitingForRecordingReconnect;
    private bool _closeWhenIdle;

    /// <summary>初始化媒体操作控件与预览交互。</summary>
    public MediaPanel()
    {
        InitializeComponent();
        InitializePreviewControls();
    }

    /// <summary>当前台操作开始或结束时，通知左栏更新操作状态。</summary>
    internal event EventHandler? BusyChanged;
    /// <summary>指示是否存在占用右栏的前台任务。</summary>
    internal bool IsBusy => _operation is not null;
    /// <summary>指示前台任务、播放或预览清理是否仍在运行。</summary>
    internal bool HasActivity => IsBusy || _preview is not null || !_previewCleanup.IsCompleted;
    /// <summary>读取右栏当前状态标题和详情。</summary>
    internal (string Title, string Detail) Status => (StatusTitle.Text, StatusDetail.Text);
    /// <summary>获取或恢复预览音量百分比和静音状态。</summary>
    internal (int Volume, bool Muted) AudioSettings
    {
        get => ((int)PreviewVolume.Value, PreviewMute.IsOn);
        set
        {
            PreviewVolume.Value = value.Volume;
            PreviewMute.IsOn = value.Muted;
            UpdateVolumeGlyph();
        }
    }

    /// <summary>绑定宿主窗口和默认输出目录。</summary>
    /// <param name="windowId">供 WinUI 文件选择器使用的窗口 ID。</param>
    /// <param name="handle">供原生选择器使用的 HWND。</param>
    /// <param name="logWindowId">日志中的窗口编号。</param>
    /// <param name="defaultOutput">未自定义时使用的输出目录。</param>
    /// <param name="showDialogAsync">按宿主窗口布局显示对话框的回调。</param>
    internal void Attach(WindowId windowId, nint handle, int logWindowId, string defaultOutput,
        Func<ContentDialog, Task<ContentDialogResult>> showDialogAsync)
    {
        _windowId = windowId;
        _handle = handle;
        _logWindowId = logWindowId;
        _debug = new DebugTrace(PortableStorage.Log, logWindowId);
        _defaultOutput = defaultOutput;
        _showDialogAsync = showDialogAsync;
    }

    /// <summary>更新媒体操作使用的工具版本。</summary>
    /// <param name="ffmpeg">当前 FFmpeg 路径。工具未安装时为空。</param>
    /// <param name="analyzer">供已解析的网页音轨下载和备用预览使用的工具。</param>
    internal void SetTools(string? ffmpeg, WebVideoAnalyzer analyzer)
    {
        _ffmpegPath = ffmpeg;
        _media = new MediaService(ffmpeg, PortableStorage.CacheDirectory, _debug);
        _webAnalyzer = analyzer;
    }

    /// <summary>输出位置变化时清除上次任务保存的文件夹。</summary>
    internal void ResetOutputFolder() => _lastResultFolder = null;

    /// <summary>更新右栏的输入快照，源变化时关闭上一条网络预览。</summary>
    /// <param name="input">当前媒体源。链接无效或尚未选择时为空。</param>
    /// <param name="error">没有可用媒体源时显示的说明。</param>
    /// <param name="idleMessage">当前模式的预览空闲提示。</param>
    internal void SetSource(Input? input, string error, string idleMessage)
    {
        PreviewIdleMessage = idleMessage;
        _sourceError = error;
        CopyLinkButton.IsEnabled = input is not null && !IsBusy;
        if (_input != input)
        {
            _input = input;
            _selectedNetwork = null;
            if (_preview is { } preview && (preview.NetworkSource is not null || preview.WebFormat is not null))
                _ = StopPreviewAsync();
        }
        if (_preview is null)
            PreviewMessage.Text = idleMessage;
    }

    private Input RequireSource() => _input ?? throw new ArgumentException(_sourceError);

    /// <summary>对同一输入只确认一次 HLS 变体，后续任务使用该变体及其请求头。</summary>
    /// <param name="input">本次任务使用的媒体快照。</param>
    /// <param name="token">取消探测的令牌。</param>
    /// <returns>HTTP 媒体的选流结果。其他协议仍直接使用输入地址。</returns>
    private async Task<MediaService.NetworkMediaSelection?> SelectInputAsync(Input input, CancellationToken token)
    {
        if (input.Source.Scheme is not ("http" or "https"))
            return null;
        await _sourceLock.WaitAsync(token);
        try
        {
            if (_input == input && _selectedNetwork is { } cached)
            {
                _debug?.Write("Media", "复用已锁定的 HLS 入口。");
                return cached;
            }
            var selection = await MediaService.SelectNetworkMediaAsync(input.Source, token, input.WebFormat?.Headers, _debug);
            _debug?.Write("Media", $"选流结束 · 协议={input.Source.Scheme} · HLS={selection.IsHls} · 固定时长={selection.HasFixedDuration} · 节目={selection.Program}。");
            if (_input == input && selection.VariantPlaylist is not null)
            {
                _selectedNetwork = selection;
                WriteWindowLog(LogLevel.Information, "Media", $"已锁定 HLS 变体 · {selection.Source.Host} · 节目 {selection.Program?.ToString() ?? "单一"}。");
            }
            return selection;
        }
        finally { _sourceLock.Release(); }
    }

    /// <summary>阻止新任务，取消前台操作并开始关闭预览。</summary>
    /// <returns>用于等待预览资源释放的任务。</returns>
    internal Task BeginCloseAsync()
    {
        _closeWhenIdle = true;
        RequestStop();
        return StopPreviewAsync();
    }

    /// <summary>在预览完成清理后释放本窗口的 LibVLC 实例。</summary>
    internal void DisposePlayer()
    {
        _vlc?.Dispose();
        _vlc = null;
    }

    private void SetControlsEnabled(bool enabled, bool allowNetworkPreview = false)
    {
        PreviewLinkButton.IsEnabled = enabled || allowNetworkPreview;
        foreach (Control control in new Control[] { DownloadButton, RecordButton, SdpButton,
            MergeButton, TranscodeButton, OpenOutputButton })
            control.IsEnabled = enabled;
        CopyLinkButton.IsEnabled = enabled && _input is not null;
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>列出源视频信息和实际发生的转码，便于比较录制与下载。</summary>
    /// <param name="folder">一段连续录制的结果。</param>
    /// <returns>可写入日志或在结果区显示的概要。</returns>
    private static string RecordingTrackHandling(RecordingFolder folder)
    {
        string handling = folder.VideoReencoded
            ? (folder.AudioReencoded ? "视频和音频已重新编码" : "视频已重新编码")
            : folder.AudioReencoded ? "音频已重新编码" : "原码流保存";
        if (folder.SourceVideo is not { } video)
            return handling;
        return $"源视频 {video} · {handling}" + (folder.AudioReencoded && !folder.VideoReencoded ? "，视频原样保存" : "");
    }

    private async void Download_Click(object sender, RoutedEventArgs e) => await RunAsync("下载中", async token =>
    {
        Input input = RequireSource();
        if (input.WebFormat is { AudioOnly: true } audio)
        {
            string audioFile = await DownloadWebAudioAsync(audio, OutputDirectory(), token);
            ShowOutput("下载完成", audioFile);
            return;
        }

        MediaService.NetworkMediaSelection? selection = await SelectInputAsync(input, token);
        string output = await _media.DownloadAsync(input.Source, OutputDirectory(), CreateProgress(), token, input.WebFormat?.Headers, selection);
        ShowOutput("下载完成", output);
    });

    // RTP 和 UDP 的预览与录制可能争用同一个接收端口。
    private async void Record_Click(object sender, RoutedEventArgs e) => await RunAsync("录制中", async token =>
    {
        Input input = RequireSource();
        Uri source = input.Source;
        string directory = OutputDirectory();
        MediaSettings settings = PortableStorage.Configuration.Current.Media;
        if (source.Scheme is "rtp" or "udp")
            await ReleaseDatagramPreviewAsync(source);
        WebVideoLink? format = input.WebFormat;
        MediaService.NetworkMediaSelection? selection = await SelectInputAsync(input, token);
        RecordingOptions options = CreateRecordingOptions(settings, format is null);
        RecordingResult recording = await _media.RecordAsync(source, directory, options, CreateProgress(), token,
            format?.IsLive, RecordingEvents(token), format?.Headers, selection);
        await FinishRecordingAsync(recording, directory, settings);
    }, allowNetworkPreview: _input is { Source.Scheme: not ("rtp" or "udp") });

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
        new(settings.PartDurationSeconds, settings.Encoder, settings.Deinterlace, settings.Encoding,
            useReconnectSettings ? settings.Reconnect : null, ReportStreamInfo: true);

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

    /// <summary>运行界面操作，并在选择器或设置操作失败时更新任务状态。</summary>
    /// <param name="action">需要执行的异步界面操作。</param>
    /// <returns>等到该操作结束且状态更新完成的任务。</returns>
    internal async Task HandlePickerAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { SetStatus("无法完成操作", ex.Message, LogLevel.Error, ex); }
    }

    private async Task<string?> PickFileAsync(string extension)
    {
        var picker = new FileOpenPicker(_windowId) { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        if (extension != "*")
            picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    /// <summary>创建能将录制重连事件同步到界面的进度回调。</summary>
    /// <param name="token">用于区分当前任务和已结束任务的令牌。</param>
    /// <returns>更新活动任务状态的回调。迟到事件仅写日志。</returns>
    private IProgress<RecordingEvent> RecordingEvents(CancellationToken token) => new Progress<RecordingEvent>(update =>
    {
        string title = update.Recovered ? "录制已恢复" : "等待重连";
        string detail = $"{update.Time:yyyy-MM-dd HH:mm:ss zzz} · {update.Detail}";
        LogLevel level = update.Recovered ? LogLevel.Information : LogLevel.Warning;
        if (_operation?.Token == token && !token.IsCancellationRequested)
        {
            _waitingForRecordingReconnect = !update.Recovered;
            SetStatus(title, detail, level);
        }
        else
            WriteWindowLog(level, "Recording", $"{title} · {detail}");
    });

    private async Task FinishRecordingAsync(RecordingResult recording, string directory, MediaSettings settings)
    {
        IReadOnlyList<RecordingFolder> folders = recording.Folders;
        _lastResultFolder = folders.Count == 1 ? folders[0].Directory : directory;
        string summary = $"已保存 {folders.Count} 个录制文件夹，共 {folders.Sum(folder => folder.PartCount)} 个分片。";
        foreach (RecordingFolder folder in folders)
            WriteWindowLog(folder.Reencoded ? LogLevel.Warning : LogLevel.Information, "Recording",
                $"录制封装 · {Path.GetFileName(folder.Directory)} · {RecordingTrackHandling(folder)}");
        if (recording.Interruption is not null)
            summary += $"{Environment.NewLine}{recording.Interruption}";
        string title = recording.Interruption is null ? "录制完成" : "录制中断，已保留分片";
        StopProgressUpdates();
        TaskProgress.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = false;
        SetStatus(title, $"{summary}{Environment.NewLine}{string.Join(Environment.NewLine, folders.Select(RecordingTrackHandling).Distinct())}{Environment.NewLine}{_lastResultFolder}",
            recording.Interruption is null ? LogLevel.Information : LogLevel.Warning);
        if (_closeWhenIdle)
            return;

        var mergePrompt = new ContentDialog
        {
            Title = "合并录制分片？",
            Content = new ScrollViewer
            {
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, new[] { summary, "" }.Concat(folders.Select(folder =>
                        $"{Path.GetFileName(folder.Directory)} · {folder.PartCount} 片 · {RecordingTrackHandling(folder)}"))),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            PrimaryButtonText = "原样合并",
            SecondaryButtonText = "重新编码后合并",
            CloseButtonText = "保留分片",
            DefaultButton = ContentDialogButton.Primary
        };
        ContentDialogResult choice = await _showDialogAsync(mergePrompt);
        if (choice == ContentDialogResult.None || _closeWhenIdle)
            return;

        _operation!.Dispose();
        // 停止录制后仍可合并已保存的分片，因此合并阶段使用新的取消令牌。
        _operation = new CancellationTokenSource();
        _debug?.Write("Merge", $"开始合并录制结果 · 目录数={folders.Count} · 重新编码={choice == ContentDialogResult.Primary}。");
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
    /// <param name="allowNetworkPreview">是否允许在任务期间打开当前媒体源的预览。</param>
    /// <returns>前台任务及其界面恢复完成的任务。若已有任务在执行，则直接返回。</returns>
    internal async Task RunAsync(string title, Func<CancellationToken, Task> action, bool allowNetworkPreview = false)
    {
        if (IsBusy || _closeWhenIdle)
            return;
        _operation = new CancellationTokenSource();
        long started = Stopwatch.GetTimestamp();
        _debug?.Write("Task", $"开始任务 · 阶段={title} · 允许并行预览={allowNetworkPreview}。");
        SetControlsEnabled(false, allowNetworkPreview);
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
            _debug?.Write("Task", $"结束任务 · 阶段={title} · 最终状态={StatusTitle.Text} · 已取消={_operation.IsCancellationRequested} · 耗时={Stopwatch.GetElapsedTime(started).TotalSeconds:F2} 秒。");
            StopProgressUpdates();
            _operation.Dispose();
            _operation = null;
            TaskProgress.Visibility = Visibility.Collapsed;
            StopButton.IsEnabled = false;
            SetControlsEnabled(true);
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

    /// <summary>创建合并后台进度并定时刷新右栏的报告器。</summary>
    /// <returns>当前任务用于发布增量进度的报告器。</returns>
    internal IProgress<MediaProgress> CreateProgress()
    {
        StopProgressUpdates();
        var updates = new PendingMediaProgress();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = true;
        string? size = null;
        string? parts = null;
        string? time = null;
        RecordingMediaProgress? recording = null;
        long? writtenBytes = null;
        TimeSpan mediaTime = TimeSpan.Zero;
        long lastDebugProgress = Stopwatch.GetTimestamp();
        var writeRate = new AverageRecordingRate();
        var videoRate = new RealtimeTrackRate();
        var audioRate = new RealtimeTrackRate();
        timer.Tick += (_, _) =>
        {
            if (_progressTimer != timer || _operation?.IsCancellationRequested != false)
                return;
            if (updates.Take() is { } progress)
                UpdateTaskProgress(progress);
            if (_waitingForRecordingReconnect)
            {
                ClearRates();
                RecordingInfo.Visibility = Visibility.Visible;
                SetRecordingText(RecordingWriteInfo, "正在重连");
                SetRecordingText(RecordingVideoInfo, null);
                SetRecordingText(RecordingAudioInfo, null);
            }
            else if (recording is { } media && (media.Video is not null || media.Audio is not null))
            {
                long now = Stopwatch.GetTimestamp();
                RecordingInfo.Visibility = Visibility.Visible;
                string mode = (media.VideoReencoded, media.AudioReencoded) switch
                {
                    (true, true) => "转码录制（视频和音频）",
                    (true, false) => "转码录制（视频）",
                    (false, true) => "转码录制（音频）",
                    _ => "原码录制"
                };
                SetRecordingText(RecordingWriteInfo, $"{mode}  ·  分片 {media.Container}  ·  平均封装码率 {writeRate.Sample(mediaTime, writtenBytes)}");
                SetRecordingText(RecordingVideoInfo, FormatTrack("视频", media.Video, videoRate.Sample(now, media.VideoBytes)));
                SetRecordingText(RecordingAudioInfo, FormatTrack("音频", media.Audio, audioRate.Sample(now, media.AudioBytes)));
            }
            else if (RecordingInfo.Visibility == Visibility.Visible)
                RecordingInfo.Visibility = Visibility.Collapsed;
        };
        _progressTimer = timer;
        timer.Start();
        return updates;

        void UpdateTaskProgress(MediaProgress progress)
        {
            if (progress.Recording is { } media)
            {
                if (recording?.Attempt != media.Attempt)
                {
                    writtenBytes = null;
                    mediaTime = TimeSpan.Zero;
                    ClearRates();
                }
                recording = media;
            }
            if (progress.Bytes is null && progress.Parts is null && progress.MediaTime is null)
                return;
            if (progress.Bytes is long bytes)
            {
                writtenBytes = bytes;
                size = progress.TotalBytes is > 0 ? $"{bytes / 1_000_000d:0.00} / {progress.TotalBytes / 1_000_000d:0.00} MB"
                    : bytes < 1_000_000 ? $"{bytes / 1000d:0.0} kB" : $"{bytes / 1_000_000d:0.00} MB";
            }
            if (progress.TotalBytes is > 0 && progress.Bytes is long received)
            {
                TaskProgress.IsIndeterminate = false;
                TaskProgress.Value = Math.Clamp(received * 100d / progress.TotalBytes.Value, 0, 100);
            }
            if (progress.Parts is int count)
                parts = $"已保存 {count} 片";
            if (progress.MediaTime is TimeSpan duration)
            {
                mediaTime = duration;
                time = $"媒体时长 {(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            }
            if (_debug?.Enabled == true && Stopwatch.GetElapsedTime(lastDebugProgress) >= TimeSpan.FromSeconds(10))
            {
                lastDebugProgress = Stopwatch.GetTimestamp();
                _debug.Write("Task", $"进度快照 · 阶段={StatusTitle.Text} · 媒体时间={mediaTime.TotalSeconds:F1} 秒 · 已写入={writtenBytes} 字节 · 已保存={parts} · 视频包字节={recording?.VideoBytes} · 音频包字节={recording?.AudioBytes}。");
            }
            string detail = string.Join("  ·  ", new[] { time, parts, size }.OfType<string>());
            if (!_waitingForRecordingReconnect && StatusDetail.Text != detail)
                StatusDetail.Text = detail;
        }

        void ClearRates()
        {
            writeRate.Clear();
            videoRate.Clear();
            audioRate.Clear();
        }
    }

    private static string? FormatTrack(string name, string? description, string rate) =>
        description is null ? null : $"{name}  {description}  ·  实时码率 {rate}";

    private static void SetRecordingText(TextBlock target, string? text)
    {
        Visibility visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (target.Visibility != visibility)
            target.Visibility = visibility;
        if (text is not null && target.Text != text)
            target.Text = text;
    }

    /// <summary>按最近约 30 秒媒体时长计算平均码率。等待新数据时保留上次读数。</summary>
    private sealed class AverageRecordingRate
    {
        private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();
        private (TimeSpan At, long Bytes) _last;
        private string _rate = "—";

        public void Clear()
        {
            _samples.Clear();
            _last = default;
            _rate = "—";
        }

        public string Sample(TimeSpan mediaTime, long? total)
        {
            if (total is not long bytes || bytes <= _last.Bytes || mediaTime - _last.At < TimeSpan.FromSeconds(1))
                return _rate;
            if (_samples.Count == 0)
                _samples.Enqueue(default);
            _last = (mediaTime, bytes);
            _samples.Enqueue(_last);
            while (_samples.Count > 2 && mediaTime - _samples.Peek().At > TimeSpan.FromSeconds(30))
                _samples.Dequeue();

            var baseline = _samples.Peek();
            _rate = FormatBitrate(bytes - baseline.Bytes, (mediaTime - baseline.At).TotalSeconds);
            return _rate;
        }
    }

    /// <summary>按最近约 3 秒实际经过的时间计算分轨实时码率，反映录制端收到的包速率。</summary>
    private sealed class RealtimeTrackRate
    {
        private readonly Queue<(long At, long Bytes)> _samples = new();

        public void Clear() => _samples.Clear();

        public string Sample(long now, long? total)
        {
            if (total is not long bytes)
                return "—";
            while (_samples.TryPeek(out var old) && Stopwatch.GetElapsedTime(old.At, now) > TimeSpan.FromSeconds(3))
                _samples.Dequeue();
            _samples.Enqueue((now, bytes));

            var baseline = _samples.Peek();
            double seconds = Stopwatch.GetElapsedTime(baseline.At, now).TotalSeconds;
            return seconds < 1 ? "—" : FormatBitrate(bytes - baseline.Bytes, seconds);
        }
    }

    private static string FormatBitrate(long bytes, double seconds)
    {
        double kbps = bytes * 8d / seconds / 1000d;
        return kbps >= 1000 ? $"{kbps / 1000:0.00} Mb/s" : $"{kbps:0} kb/s";
    }

    private void StopProgressUpdates()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
        _waitingForRecordingReconnect = false;
        RecordingInfo.Visibility = Visibility.Collapsed;
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
                        value.Parts ?? previous.Parts, value.MediaTime ?? previous.MediaTime,
                        value.Recording ?? previous.Recording)
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

    /// <summary>更新右栏状态，并按指定级别记录该变化。</summary>
    /// <param name="title">状态标题。</param>
    /// <param name="detail">可供用户查看的详情。</param>
    /// <param name="level">用于输出状态的日志级别。</param>
    /// <param name="error">相关的可选异常。</param>
    internal void SetStatus(string title, string detail, LogLevel level = LogLevel.Information, Exception? error = null)
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

    /// <summary>解析媒体任务使用的自定义或默认输出目录。</summary>
    /// <returns>已规范化的目录路径。</returns>
    internal string OutputDirectory() =>
        NormalizeOutputDirectory(PortableStorage.Configuration.Current.Media.OutputDirectory ?? _defaultOutput);

    /// <summary>校验输出目录输入并规范化为绝对路径。</summary>
    /// <param name="value">来自配置或设置表单的目录。</param>
    /// <returns>处理首尾空白和环境变量后的路径。</returns>
    /// <exception cref="ArgumentException">目录在展开环境变量后仍不是完整路径。</exception>
    internal static string NormalizeOutputDirectory(string value)
    {
        string directory = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("请选择有效的输出文件夹。");
        return Path.GetFullPath(directory);
    }

    private async Task<string> DownloadWebAudioAsync(WebVideoLink audio, string directory, CancellationToken token)
    {
        DirectoryInfo work = Directory.CreateDirectory(Path.Combine(PortableStorage.CacheDirectory, $"audio-{Guid.NewGuid():N}"));
        // 直接下载的音频先留在缓存。输出失败时保留已完成的文件供恢复。
        bool preserveDownload = false;
        try
        {
            SetStatus("正在下载音频", "yt-dlp 正在下载所选音轨并准备 M4A 文件。");
            string file = await _webAnalyzer.DownloadAudioAsync(audio, work.FullName, _ffmpegPath, token);
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => MediaService.SaveDownloadedAudio(file, directory, DateTime.Now), token);
        }
        catch (MediaPublishException) { preserveDownload = true; throw; }
        finally { if (!preserveDownload) DeleteWebAudioFiles(work); }
    }

    private void DeleteWebAudioFiles(DirectoryInfo folder)
    {
        try { folder.Delete(recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteWindowLog(LogLevel.Warning, "Download", "无法清理音频缓存。", ex);
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_input is not { } input)
            return;
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(input.Source.AbsoluteUri);
            Clipboard.SetContent(data);
            SetStatus("已复制直链", "可粘贴到其他播放器或下载工具。");
        }
        catch (Exception ex) { SetStatus("无法复制直链", ex.Message, LogLevel.Error, ex); }
    }
}
