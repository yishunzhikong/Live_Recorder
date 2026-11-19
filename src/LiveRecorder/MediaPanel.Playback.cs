using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using LiveRecorder.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SharpDX.DXGI;
using LogLevel = LiveRecorder.Core.LogLevel;

namespace LiveRecorder;

// 管理 LibVLC 预览、播放控件，以及直接播放和子进程供流之间的切换。
public sealed partial class MediaPanel
{
    private const string PreviewIdle = "选择预览链接或打开本地视频。";
    // VLC 3 的 WinRT 视频输出通过这两个 DXGI 私有数据项读取渲染尺寸。
    private static readonly Guid SwapChainWidth = new("f1b59347-1643-411a-ad6b-c780177a06b6");
    private static readonly Guid SwapChainHeight = new("6ea976a0-9d60-4bb7-a5a9-7dd1187fc9bd");
    private readonly TaskCompletionSource<string[]> _videoSurface = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _previewSwapChain;
    private SwapChainPanel? _previewSurface;
    private LibVLC? _vlc;
    private bool _verboseVlc;
    private int _nativeDiagnosticsRemaining;
    private PreviewSession? _preview;
    private DispatcherQueueTimer? _previewClock;
    private Task _previewCleanup = Task.CompletedTask;
    private string? _lastResultFile;
    private bool _updatingSeek;
    private bool _draggingSeek;

    /// <summary>报告从预览卡片顶部到操作卡片底边的可见高度。</summary>
    internal event Action<double>? VisibleHeightChanged;

    // 每次预览独立持有播放器、输入和取消令牌，便于忽略旧会话的延迟回调。
    private sealed class PreviewSession(Uri? networkSource, WebVideoLink? webFormat)
    {
        public Uri? NetworkSource { get; } = networkSource;
        public WebVideoLink? WebFormat { get; } = webFormat;
        public bool HasFixedDuration { get; set; } = networkSource is null && webFormat is null;
        public MediaService.NetworkMediaSelection? Selection { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Startup { get; set; } = Task.CompletedTask;
        public Media? Media { get; set; }
        public MediaPlayer? Player { get; set; }
        public StreamMediaInput? Input { get; set; }
        public PreviewStream? Producer { get; private set; }
        public bool Opened { get; set; }
        public long Started { get; } = Stopwatch.GetTimestamp();
        public Func<Task>? Replay { get; set; }
        public Func<Task>? Fallback { get; set; }

        public Media FromStream(LibVLC vlc, PreviewStream producer, params string[] options)
        {
            Producer = producer;
            Input = new StreamMediaInput(producer.Content);
            return new Media(vlc, Input, options);
        }
    }

    private void InitializePreviewControls()
    {
        PreviewSeek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _draggingSeek = true), true);
        PointerEventHandler endDrag = (_, _) => _draggingSeek = false;
        PreviewSeek.AddHandler(UIElement.PointerReleasedEvent, endDrag, true);
        PreviewSeek.AddHandler(UIElement.PointerCanceledEvent, endDrag, true);
        PreviewSeek.AddHandler(UIElement.PointerCaptureLostEvent, endDrag, true);
    }

    private void PreviewPlayer_Initialized(object sender, InitializedEventArgs e)
    {
        const string prefix = "--winrt-swapchain=0x";
        string? option = Array.Find(e.SwapChainOptions, argument => argument.StartsWith(prefix, StringComparison.Ordinal));
        if (option is not null)
            nint.TryParse(option.AsSpan(prefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _previewSwapChain);
        if (VisualTreeHelper.GetChild(PreviewPlayer, 0) is SwapChainPanel panel && _previewSurface != panel)
        {
            DetachPreviewSurface();
            _previewSurface = panel;
            panel.SizeChanged += PreviewSurface_SizeChanged;
            panel.CompositionScaleChanged += PreviewSurface_ScaleChanged;
        }
        UpdatePreviewSurfaceSize();
        _videoSurface.TrySetResult(e.SwapChainOptions);
    }

    private void PreviewPlayer_Unloaded(object sender, RoutedEventArgs e)
    {
        _previewSwapChain = 0;
        DetachPreviewSurface();
    }

    private void DetachPreviewSurface()
    {
        if (_previewSurface is not { } panel)
            return;
        panel.SizeChanged -= PreviewSurface_SizeChanged;
        panel.CompositionScaleChanged -= PreviewSurface_ScaleChanged;
        _previewSurface = null;
    }

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewSurfaceSize();
    private void PreviewSurface_ScaleChanged(SwapChainPanel sender, object args) => UpdatePreviewSurfaceSize();

    private void UpdatePreviewSurfaceSize()
    {
        if (_previewSwapChain == 0 || _previewSurface is not { ActualWidth: > 0, ActualHeight: > 0 } panel)
            return;

        // Viewbox 缩放只改变合成比例。VLC 仍需通过原生 DXGI 标识读取新的宽高。
        int width = (int)Math.Ceiling(panel.ActualWidth * panel.CompositionScaleX);
        int height = (int)Math.Ceiling(panel.ActualHeight * panel.CompositionScaleY);
        Marshal.AddRef(_previewSwapChain);
        using var swapChain = new SwapChain1(_previewSwapChain);
        using var size = new SharpDX.DataStream(sizeof(int), canRead: false, canWrite: true);
        Marshal.WriteInt32(size.DataPointer, width);
        swapChain.SetPrivateData(SwapChainWidth, sizeof(int), size.DataPointer);
        Marshal.WriteInt32(size.DataPointer, height);
        swapChain.SetPrivateData(SwapChainHeight, sizeof(int), size.DataPointer);
    }

    /// <summary>在操作区和播放控件占用空间之外，尽量放大 16:9 画面。</summary>
    /// <param name="sender">尺寸变化的右栏、标题栏或播放控制栏。</param>
    /// <param name="e">触发重新计算的尺寸变化。</param>
    private void PreviewLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double aspectRatio = 16d / 9d;
        Thickness padding = PreviewCard.Padding;
        Thickness margin = PreviewBody.Margin;
        double horizontalChrome = padding.Left + padding.Right + margin.Left + margin.Right;
        double verticalChrome = padding.Top + padding.Bottom + margin.Top + margin.Bottom +
            PreviewHeader.ActualHeight + PreviewTransport.ActualHeight;
        double availableHeight = RightLayout.ActualHeight - RightStack.Spacing - OperationsCard.Height - verticalChrome;
        double width = Math.Max(0, Math.Min(RightLayout.ActualWidth - horizontalChrome, availableHeight * aspectRatio));
        PreviewFrame.Height = width / aspectRatio;
        PreviewCard.Width = width + horizontalChrome;
    }

    // 右栏在固定逻辑尺寸中顺序布局。只在预览卡片高度变动时同步左栏底边。
    private void PreviewCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height != e.PreviousSize.Height)
            VisibleHeightChanged?.Invoke(e.NewSize.Height + RightStack.Spacing + OperationsCard.Height);
    }

    private async void PreviewLink_Click(object sender, RoutedEventArgs e)
    {
        Input input;
        try { input = RequireSource(); }
        catch (ArgumentException ex)
        {
            if (_preview is null)
                PreviewMessage.Text = ex.Message;
            else
                SetStatus("无法打开预览链接", ex.Message, LogLevel.Warning);
            WriteWindowLog(LogLevel.Debug, "Playback", "预览地址校验失败。", ex);
            return;
        }

        if (input.WebFormat is { AudioOnly: true } audio)
        {
            await PreviewWebAudioAsync(audio);
            return;
        }
        Uri source = input.Source;
        WebVideoLink? format = input.WebFormat;
        MediaRequestHeaders? headers = format?.Headers;
        await OpenPreviewAsync($"网络预览 · {source.Scheme.ToUpperInvariant()}", source, (vlc, session) =>
        {
            MediaService.NetworkMediaSelection? selection = session.Selection;
            Uri input = selection?.Source ?? source;
            // 独立音视频轨道由 FFmpeg 按节目配对。VLC 的 HTTP 输入也无法发送 Origin。
            if ((selection?.Program is not null && File.Exists(_ffmpegPath)) || headers?.Origin is not null ||
                source.Scheme is "rtmp" or "rtmps" or "rtmpt" or "rtmpts" or "rtsps")
                return session.FromStream(vlc, _media.OpenNetworkPreview(input, session.Cancellation.Token, headers,
                    hls: selection?.IsHls == true, program: selection?.Program), ":demux=ts");
            // VLC treats the address after @ as the local RTP/UDP receive address.
            string location = (source.Scheme is "rtp" or "udp") && source.UserInfo.Length == 0
                ? $"{source.Scheme}://@{source.Authority}" : input.AbsoluteUri;
            return CreateNetworkMedia(vlc, location, headers);
        }, format, input: input);
    }

    private Task PreviewWebAudioAsync(WebVideoLink audio)
    {
        var source = new Uri(audio.Url);
        if (audio.Headers?.Origin is not null)
            return PlayAudioStreamAsync(audio);
        return OpenPreviewAsync(audio.Label, source, (vlc, _) => CreateNetworkMedia(vlc, source.AbsoluteUri, audio.Headers), audio,
            () => PlayAudioStreamAsync(audio));
    }

    private static Media CreateNetworkMedia(LibVLC vlc, string location, MediaRequestHeaders? headers)
    {
        var media = new Media(vlc, location, FromType.FromLocation);
        if (headers?.UserAgent is { } userAgent)
            media.AddOption(":http-user-agent=" + userAgent);
        if (headers?.Referer is { } referer)
            media.AddOption(":http-referrer=" + referer);
        return media;
    }

    private static bool IsDatagramPreview(Uri source)
    {
        if (source.Scheme is "rtp" or "udp")
            return true;
        // IPTV 网关也会通过 /rtp/组播地址:端口 等 HTTP 路径提供媒体流。
        ReadOnlySpan<char> path = source.AbsolutePath;
        return source.Scheme is "http" or "https" &&
            (path.StartsWith("/rtp/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/udp/", StringComparison.OrdinalIgnoreCase)) &&
            IPEndPoint.TryParse(path[5..], out _);
    }

    private Task PlayAudioStreamAsync(WebVideoLink audio) => OpenPreviewAsync(audio.Label, null, (vlc, session) =>
    {
        PreviewMessage.Text = "正在缓冲音频…";
        return session.FromStream(vlc, _webAnalyzer.OpenAudioPreview(audio, _ffmpegPath, session.Cancellation.Token));
    }, audio);

    private async void PlayFile_Click(object sender, RoutedEventArgs e) => await HandlePickerAsync(async () =>
    {
        if (await PickFileAsync("*") is { } file)
            await PlayLocalAsync(file);
    });

    private async void PlayResult_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResultFile is { } file)
            await PlayLocalAsync(file);
    }

    private Task PlayLocalAsync(string file) => OpenPreviewAsync(Path.GetFileName(file), null, (vlc, _) =>
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("找不到要播放的媒体文件。", file);
        return new Media(vlc, file, FromType.FromPath);
    });

    /// <summary>创建预览会话，并在旧会话清理后重新使用视频区域。</summary>
    /// <param name="title">预览区域显示的名称。</param>
    /// <param name="networkSource">用于判断网络协议和总时长的源地址。本地或管道音频可为空。</param>
    /// <param name="loadMedia">使用 LibVLC 和新会话构造媒体对象的回调。</param>
    /// <param name="format">可选的 yt-dlp 格式与请求头信息。</param>
    /// <param name="fallback">可选的操作，在当前媒体尚未开始播放便失败时执行。</param>
    /// <param name="input">可选的当前网络媒体快照，用于共享 HLS 选流。</param>
    /// <returns>完成启动处理的任务。任务返回时播放器可能仍在缓冲。</returns>
    private async Task OpenPreviewAsync(string title, Uri? networkSource, Func<LibVLC, PreviewSession, Media> loadMedia,
        WebVideoLink? format = null, Func<Task>? fallback = null, Input? input = null)
    {
        if (_closeWhenIdle)
            return;

        // 在复用视频区域前先关闭旧会话。新会话会等待旧资源释放。
        Task previous = StopPreviewAsync();
        var session = new PreviewSession(networkSource, format)
        {
            Replay = () => OpenPreviewAsync(title, networkSource, loadMedia, format, fallback, input),
            Fallback = fallback
        };
        _preview = session;
        string source = format is not null ? "yt-dlp" : networkSource?.Scheme ?? "本地文件";
        WriteWindowLog(LogLevel.Information, "Playback", $"正在打开预览。来源 {source}。");
        PreviewName.Text = title;
        PreviewMessage.Text = "正在打开媒体…";
        StopPreviewButton.Visibility = Visibility.Visible;
        session.Startup = LoadAndPlayAsync();
        try { await session.Startup; }
        catch (Exception ex)
        {
            if (_preview == session)
                await PreviewFailedAsync(session, ex is TimeoutException ? "VLC 视频界面初始化超时。" : ex.Message);
        }

        async Task LoadAndPlayAsync()
        {
            await Task.Yield();
            await previous;
            CancellationToken token = session.Cancellation.Token;
            if (input is not null)
            {
                session.Selection = await SelectInputAsync(input, token);
                session.HasFixedDuration = session.Selection?.HasFixedDuration == true && format?.IsLive != true;
            }
            string[] display = await _videoSurface.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            token.ThrowIfCancellationRequested();
            // 自动识别隔行画面，再按场输出，保留 1080i 等电视直播的运动流畅度。
            bool verbose = _debug?.Enabled == true;
            if (_vlc is not null && verbose != _verboseVlc)
                DisposePlayer();
            if (_vlc is null)
            {
                _vlc = new LibVLC([.. display, "--ignore-config", "--no-plugins-cache", "--no-video-title-show",
                    "--deinterlace=-1", "--deinterlace-mode=yadif2x", .. (verbose ? new[] { "--verbose=2" } : [])]);
                _verboseVlc = verbose;
                if (verbose)
                    _vlc.Log += VlcDiagnostic;
            }
            Interlocked.Exchange(ref _nativeDiagnosticsRemaining, 40);
            PreviewPlayer.Clear();
            Media media = session.Media = loadMedia(_vlc, session);
            ApplyPreviewDecoder(media, session.NetworkSource);
            WriteWindowLog(LogLevel.Debug, "Playback", session.Producer is null ? "使用 VLC 直接读取媒体。" : "使用管道为 VLC 提供媒体。");
            // VLC 根据原始画幅等比例适应 VideoView，竖屏与宽银幕视频共用 16:9 预览区。
            var player = new MediaPlayer(_vlc) { Scale = 0 };
            session.Player = player;
            player.Playing += (_, _) => DispatcherQueue.TryEnqueue(() => PreviewPlaying(session, player));
            player.Paused += (_, _) => DispatcherQueue.TryEnqueue(() => PreviewInactive(session, player));
            player.EndReached += (_, _) => DispatcherQueue.TryEnqueue(() => PreviewInactive(session, player, ended: true));
            player.Stopped += (_, _) => DispatcherQueue.TryEnqueue(() => PreviewInactive(session, player));
            player.EncounteredError += (_, _) => DispatcherQueue.TryEnqueue(() => _ = PreviewFailedAsync(session, ""));
            PreviewPlayer.MediaPlayer = player;
            if (!player.Play(media))
                throw new InvalidOperationException("VLC 无法开始播放此媒体。");
            if (session.Producer is { } producer)
                _ = ObserveStreamAsync(session, producer.Completion);
            else if (session.Selection is null && networkSource is not null && format?.IsLive != true)
                _ = CheckPreviewDurationAsync(session, networkSource, token);
            // 播放请求成功后仍需监控 VLC 是否真正进入播放状态。
            _ = WatchPreviewStartupAsync(session, token);
        }
    }

    private void VlcDiagnostic(object? sender, LogEventArgs args)
    {
        if (_debug?.Enabled != true || Volatile.Read(ref _nativeDiagnosticsRemaining) <= 0)
            return;
        // 只记录解码与渲染诊断，避免把媒体地址、请求头和逐帧跟踪传入日志。
        string message = args.Message ?? "";
        if (message.StartsWith("Using D3D11VA", StringComparison.Ordinal) ||
            message.StartsWith("Using DXVA2", StringComparison.Ordinal) ||
            message.StartsWith("using video decoder module", StringComparison.Ordinal) ||
            message.StartsWith("using vout display module", StringComparison.Ordinal) ||
            message.StartsWith("deinterlace ", StringComparison.Ordinal) ||
            message.StartsWith("Detected interlaced video", StringComparison.Ordinal) ||
            message.StartsWith("Failed to compensate for the format changes", StringComparison.Ordinal) ||
            message.StartsWith("picture is too late", StringComparison.Ordinal))
        {
            if (Interlocked.Decrement(ref _nativeDiagnosticsRemaining) >= 0)
                _debug.Write("LibVLC", $"{args.Module} · {args.Level} · {message}");
        }
    }

    private void ApplyPreviewDecoder(Media media, Uri? source)
    {
        PreviewDecoder decoder = PortableStorage.Configuration.Current.Media.PreviewDecoder;
        // 自动模式沿用 IPTV 软件解码，避免硬解后的 D3D11 格式转换让 VLC 移除双倍去隔行滤镜。
        bool software = decoder == PreviewDecoder.Software ||
            decoder == PreviewDecoder.Automatic && source is not null && IsDatagramPreview(source);
        _debug?.Write("Playback", $"预览解码 · 设置={decoder} · VLC 请求={(software ? "软件解码" : "自动选择硬件")} · 去隔行=自动 Yadif 2x。");
        if (software)
            media.AddOption(":avcodec-hw=none");
        else if (decoder == PreviewDecoder.HardwarePreferred)
            media.AddOption(":avcodec-hw=any");
    }

    // HTTP 直播可能报告有限的缓存窗口。确认媒体有固定长度后才显示总时长。
    private async Task CheckPreviewDurationAsync(PreviewSession session, Uri source, CancellationToken token)
    {
        if (await MediaService.HasFixedPreviewDurationAsync(source, token, session.WebFormat?.Headers) && _preview == session)
        {
            session.HasFixedDuration = true;
            if (session is { Opened: true, Player: { } player } && player.State is not (VLCState.Ended or VLCState.Stopped))
                RefreshPreviewTimeline(session, player);
        }
    }

    private void PreviewPlaying(PreviewSession session, MediaPlayer player)
    {
        if (_preview != session)
            return;
        if (!session.Opened)
        {
            session.Opened = true;
            WriteWindowLog(LogLevel.Information, "Playback", "预览开始播放。");
            _debug?.Write("Playback", $"播放器已进入播放 · 固定时长={session.HasFixedDuration} · 启动耗时={Stopwatch.GetElapsedTime(session.Started).TotalMilliseconds:F0} 毫秒。");
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewPlay.IsEnabled = PreviewVolumeButton.IsEnabled = true;
            SetPreviewVolume(player);
        }
        _previewClock ??= CreatePreviewClock();
        _previewClock.Start();
        SetPreviewPlayButton(true);
        RefreshPreviewTimeline(session, player);
    }

    private void PreviewInactive(PreviewSession session, MediaPlayer player, bool ended = false)
    {
        if (_preview != session || (!ended && player.IsPlaying))
            return;
        _debug?.Write("Playback", $"播放状态变化 · 状态={player.State} · 自然结束={ended} · 媒体位置={player.Time} 毫秒。");
        _previewClock?.Stop();
        SetPreviewPlayButton(false);
        if (ended && session.HasFixedDuration && player.Length > 0)
        {
            PreviewTime.Text = $"{FormatTime(player.Length)} / {FormatTime(player.Length)}";
            SetSeekPosition(PreviewSeek.Maximum);
        }
        else if (player.State == VLCState.Paused)
        {
            RefreshPreviewTimeline(session, player);
        }
        if (player.State != VLCState.Paused)
            PreviewSeek.IsEnabled = false;
    }

    private DispatcherQueueTimer CreatePreviewClock()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (_preview is { Player: { } player } session)
                RefreshPreviewTimeline(session, player);
        };
        return timer;
    }

    private void SetPreviewPlayButton(bool playing)
    {
        string glyph = playing ? "\uE769" : "\uE768";
        if (PreviewPlayGlyph.Glyph == glyph)
            return;
        PreviewPlayGlyph.Glyph = glyph;
        AutomationProperties.SetName(PreviewPlay, playing ? "暂停" : "播放");
    }

    private void RefreshPreviewTimeline(PreviewSession session, MediaPlayer player)
    {
        long elapsed = Math.Max(0, player.Time);
        long duration = session.HasFixedDuration ? player.Length : 0;
        PreviewSeek.IsEnabled = player.IsSeekable && duration > 0;
        string time = duration > 0 ? $"{FormatTime(elapsed)} / {FormatTime(duration)}" : FormatTime(elapsed);
        if (time != PreviewTime.Text)
            PreviewTime.Text = time;
        if (!_draggingSeek)
            SetSeekPosition(duration > 0 ? elapsed * PreviewSeek.Maximum / duration : 0);
    }

    private static string FormatTime(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
    }

    // 程序同步进度条时禁止触发用户拖动的定位逻辑。
    private void SetSeekPosition(double value)
    {
        _updatingSeek = true;
        try { PreviewSeek.Value = Math.Clamp(value, 0, PreviewSeek.Maximum); }
        finally { _updatingSeek = false; }
    }

    private void PreviewSeek_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingSeek && PreviewSeek.IsEnabled && _preview?.Player is { IsSeekable: true } player)
        {
            double position = e.NewValue / PreviewSeek.Maximum;
            player.Position = (float)position;
            if (player.State == VLCState.Paused && player.Length > 0)
                PreviewTime.Text = $"{FormatTime((long)(player.Length * position))} / {FormatTime(player.Length)}";
        }
    }

    private async void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is not { Player: { } player } session)
            return;
        if (player.State is VLCState.Ended or VLCState.Stopped)
        {
            if (session.Replay is { } replay)
                await replay();
        }
        else
        {
            player.SetPause(player.IsPlaying);
        }
    }

    private void PreviewVolume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_preview?.Player is { } player)
        {
            player.Volume = (int)e.NewValue;
            UpdateVolumeGlyph();
        }
    }

    private void PreviewMute_Toggled(object sender, RoutedEventArgs e)
    {
        if (_preview?.Player is { } player)
        {
            player.Mute = PreviewMute.IsOn;
            UpdateVolumeGlyph();
        }
    }

    private void SetPreviewVolume(MediaPlayer player)
    {
        player.Volume = (int)PreviewVolume.Value;
        player.Mute = PreviewMute.IsOn;
        UpdateVolumeGlyph();
    }

    private void UpdateVolumeGlyph() => PreviewVolumeGlyph.Glyph = PreviewMute.IsOn || PreviewVolume.Value == 0 ? "\uE74F" : "\uE767";

    private async Task WatchPreviewStartupAsync(PreviewSession session, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), token); }
        catch (OperationCanceledException) { return; }
        if (!session.Opened)
            await PreviewFailedAsync(session, "连接超时，请检查媒体链接。");
    }

    private async Task ObserveStreamAsync(PreviewSession session, Task completion)
    {
        try { await completion; }
        catch (Exception ex)
        {
            if (_preview == session)
                await PreviewFailedAsync(session, ex.Message);
        }
    }

    private async Task PreviewFailedAsync(PreviewSession session, string message)
    {
        if (_preview != session)
            return;
        if (!session.Opened && session.Fallback is { } fallback)
        {
            WriteWindowLog(LogLevel.Warning, "Playback", "音频直放失败，切换到备用媒体管道。");
            session.Fallback = null;
            await fallback();
            return;
        }
        WriteWindowLog(LogLevel.Error, "Playback", string.IsNullOrWhiteSpace(message) ? "VLC 无法播放此媒体。" : message);
        Task cleanup = StopPreviewAsync();
        PreviewMessage.Text = string.IsNullOrWhiteSpace(message) ? "VLC 无法播放此媒体。" : $"无法播放此媒体。{Environment.NewLine}{message}";
        await cleanup;
    }

    private async void StopPreview_Click(object sender, RoutedEventArgs e) => await StopPreviewAsync();

    // 同一 RTP 或 UDP 端口开始录制前，先释放占用该端口的预览。
    private Task ReleaseDatagramPreviewAsync(Uri? source = null) =>
        _preview?.NetworkSource is { Scheme: "rtp" or "udp" } active &&
        (source is null || active.Port == source.Port) ? StopPreviewAsync() : _previewCleanup;

    /// <summary>解除当前预览与界面的关联并开始释放其资源。</summary>
    /// <returns>等待预览资源释放的任务。若已开始清理则返回正在运行的清理任务。</returns>
    internal Task StopPreviewAsync()
    {
        if (_preview is not { } session)
            return _previewCleanup;
        _preview = null;
        WriteWindowLog(LogLevel.Debug, "Playback", "正在关闭预览。");
        _previewClock?.Stop();
        _draggingSeek = false;
        PreviewPlay.IsEnabled = PreviewVolumeButton.IsEnabled = false;
        SetPreviewPlayButton(false);
        PreviewSeek.IsEnabled = false;
        SetSeekPosition(0);
        PreviewTime.Text = "0:00:00";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewMessage.Text = PreviewIdleMessage;
        PreviewName.Text = "";
        StopPreviewButton.Visibility = Visibility.Collapsed;
        _previewCleanup = CleanupAsync(session);
        return _previewCleanup;
    }

    private async Task CleanupAsync(PreviewSession session)
    {
        try
        {
            // Cancel the producer first so VLC can exit a blocking read from the audio pipe.
            try { await session.Cancellation.CancelAsync(); }
            catch (Exception ex) { WriteWindowLog(LogLevel.Warning, "Playback", "无法取消预览输入。", ex); }
            try { await session.Startup; }
            catch (Exception) { /* The preview request reports startup errors. */ }
            if (session.Player is { } player)
            {
                try { await Task.Run(player.Stop); }
                finally
                {
                    try { PreviewPlayer.MediaPlayer = null; }
                    finally { DisposePreviewResource(player); }
                }
            }
        }
        catch (Exception ex) { WriteWindowLog(LogLevel.Warning, "Playback", "无法完整清理预览。", ex); }
        finally
        {
            DisposePreviewResource(session.Media);
            DisposePreviewResource(session.Input);
            try
            {
                if (session.Producer is { } producer)
                    await producer.DisposeAsync();
            }
            catch (Exception ex) { WriteWindowLog(LogLevel.Warning, "Playback", "无法清理预览输入进程。", ex); }
            finally { DisposePreviewResource(session.Cancellation); }
        }
    }

    private void DisposePreviewResource(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { WriteWindowLog(LogLevel.Warning, "Playback", "无法释放预览资源。", ex); }
    }
}
