using System.Diagnostics;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using LiveRecorder.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace LiveRecorder;

public sealed partial class MainWindow
{
    private const string PreviewIdle = "选择预览链接或打开本地视频。";
    private readonly TaskCompletionSource<string[]> _videoSurface = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LibVLC? _vlc;
    private PreviewSession? _preview;
    private DispatcherQueueTimer? _previewClock;
    private Task _previewCleanup = Task.CompletedTask;
    private string? _lastResultFile;
    private bool _updatingSeek;
    private bool _draggingSeek;

    private sealed class PreviewSession(Uri? networkSource, WebVideoLink? webFormat)
    {
        public Uri? NetworkSource { get; } = networkSource;
        public WebVideoLink? WebFormat { get; } = webFormat;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Startup { get; set; } = Task.CompletedTask;
        public Media? Media { get; set; }
        public MediaPlayer? Player { get; set; }
        public StreamMediaInput? Input { get; set; }
        public PreviewStream? Producer { get; private set; }
        public bool Opened { get; set; }
        public Func<Task>? Replay { get; set; }

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
        Closed += (_, _) => _vlc?.Dispose();
    }

    private void PreviewPlayer_Initialized(object sender, InitializedEventArgs e) => _videoSurface.TrySetResult(e.SwapChainOptions);

    private async void PreviewLink_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWebAudio is { } audio)
        {
            await PreviewWebAudioAsync(audio);
            return;
        }

        Uri source;
        try { source = SelectedMediaSource(); }
        catch (ArgumentException ex)
        {
            if (_preview is null)
                PreviewMessage.Text = ex.Message;
            else
                SetStatus("无法打开预览链接", ex.Message);
            return;
        }

        await OpenPreviewAsync($"网络预览 · {source.Scheme.ToUpperInvariant()}", source, (vlc, session) =>
        {
            // The Windows LibVLC package has no RTMP access plugin.
            if (source.Scheme is "rtmp" or "rtmps" or "rtmpt" or "rtmpts" or "rtsps")
                return session.FromStream(vlc, _media.OpenNetworkPreview(source, session.Cancellation.Token), ":demux=ts");
            // VLC treats the address after @ as the local RTP/UDP receive address.
            string location = (source.Scheme is "rtp" or "udp") && source.UserInfo.Length == 0
                ? $"{source.Scheme}://@{source.Authority}" : source.AbsoluteUri;
            return new Media(vlc, location, FromType.FromLocation);
        }, SelectedWebFormat);
    }

    private Task PreviewWebAudioAsync(WebVideoLink audio) => OpenPreviewAsync(audio.Label, null, (vlc, session) =>
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

    private async Task OpenPreviewAsync(string title, Uri? networkSource, Func<LibVLC, PreviewSession, Media> loadMedia,
        WebVideoLink? format = null)
    {
        if (_closeWhenIdle)
            return;

        Task previous = StopPreviewAsync();
        var session = new PreviewSession(networkSource, format)
        {
            Replay = () => OpenPreviewAsync(title, networkSource, loadMedia, format)
        };
        _preview = session;
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
            string[] display = await _videoSurface.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            token.ThrowIfCancellationRequested();
            _vlc ??= new LibVLC([.. display, "--ignore-config", "--no-plugins-cache", "--no-video-title-show"]);
            PreviewPlayer.Clear();
            Media media = session.Media = loadMedia(_vlc, session);
            var player = new MediaPlayer(_vlc);
            session.Player = player;
            player.Playing += (_, _) => DispatcherQueue.TryEnqueue(() => PreviewPlaying(session, player));
            player.EncounteredError += (_, _) => DispatcherQueue.TryEnqueue(() => _ = PreviewFailedAsync(session, ""));
            PreviewPlayer.MediaPlayer = player;
            if (!player.Play(media))
                throw new InvalidOperationException("VLC 无法开始播放此媒体。");
            if (session.Producer is { } producer)
                _ = ObserveStreamAsync(session, producer.Completion);
            _ = WatchPreviewStartupAsync(session, token);
        }
    }

    private void PreviewPlaying(PreviewSession session, MediaPlayer player)
    {
        if (_preview != session)
            return;
        if (!session.Opened)
        {
            session.Opened = true;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewTransport.Visibility = Visibility.Visible;
            SetPreviewVolume(player);
            _previewClock ??= CreatePreviewClock();
            _previewClock.Start();
        }
        RefreshPreviewControls(session, player);
    }

    private DispatcherQueueTimer CreatePreviewClock()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (_preview is { Player: { } player } session)
                RefreshPreviewControls(session, player);
        };
        return timer;
    }

    private void RefreshPreviewControls(PreviewSession session, MediaPlayer player)
    {
        bool playing = player.IsPlaying;
        PreviewPlayGlyph.Glyph = playing ? "\uE769" : "\uE768";
        AutomationProperties.SetName(PreviewPlay, playing ? "暂停" : "播放");
        long elapsed = Math.Max(0, player.Time);
        long duration = session.WebFormat?.IsLive == true ? 0 : player.Length;
        PreviewSeek.IsEnabled = player.IsSeekable && duration > 0;
        PreviewTime.Text = $"{FormatTime(elapsed)} / {(duration > 0 ? FormatTime(duration) : "--:--")}";
        if (!_draggingSeek)
            SetSeekPosition(duration > 0 ? elapsed * PreviewSeek.Maximum / duration : 0);
        if (session.NetworkSource is { } source)
        {
            var current = (Source: source, Format: session.WebFormat);
            int videoTracks = player.VideoTrackCount;
            if (videoTracks > 0 && _detectedAudio == current)
            {
                _detectedAudio = null;
                UpdateRecordButton();
            }
            else if (videoTracks == 0 && _detectedAudio != current && player.AudioTrackCount > 0)
            {
                _detectedAudio = current;
                UpdateRecordButton();
            }
        }
    }

    private static string FormatTime(long milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private void SetSeekPosition(double value)
    {
        _updatingSeek = true;
        try { PreviewSeek.Value = Math.Clamp(value, 0, PreviewSeek.Maximum); }
        finally { _updatingSeek = false; }
    }

    private void PreviewSeek_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingSeek && PreviewSeek.IsEnabled && _preview?.Player is { IsSeekable: true } player)
            player.Position = (float)(e.NewValue / PreviewSeek.Maximum);
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
        Task cleanup = StopPreviewAsync();
        PreviewMessage.Text = string.IsNullOrWhiteSpace(message) ? "VLC 无法播放此媒体。" : $"无法播放此媒体。{Environment.NewLine}{message}";
        await cleanup;
    }

    private async void StopPreview_Click(object sender, RoutedEventArgs e) => await StopPreviewAsync();

    private Task ReleaseDatagramPreviewAsync(Uri? source = null) =>
        _preview?.NetworkSource is { Scheme: "rtp" or "udp" } active &&
        (source is null || active.Port == source.Port) ? StopPreviewAsync() : _previewCleanup;

    private Task StopPreviewAsync()
    {
        if (_preview is not { } session)
            return _previewCleanup;
        _preview = null;
        _previewClock?.Stop();
        _draggingSeek = false;
        PreviewTransport.Visibility = Visibility.Collapsed;
        PreviewSeek.IsEnabled = false;
        SetSeekPosition(0);
        PreviewTime.Text = "0:00:00 / --:--";
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
            catch (Exception ex) { Debug.WriteLine(ex); }
            try { await session.Startup; }
            catch (Exception) { /* The preview request reports startup errors. */ }
            if (session.Player is { } player)
            {
                try { await Task.Run(player.Stop); }
                finally
                {
                    PreviewPlayer.MediaPlayer = null;
                    player.Dispose();
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            session.Media?.Dispose();
            session.Input?.Dispose();
            if (session.Producer is { } producer)
                await producer.DisposeAsync();
            session.Cancellation.Dispose();
        }
    }
}
