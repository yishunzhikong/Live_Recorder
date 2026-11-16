using LiveRecorder.Core;
using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace LiveRecorder;

public sealed partial class MainWindow
{
    private const string PreviewIdle = "选择预览链接或打开本地视频。";
    private PreviewSession? _preview;
    private Task _previewCleanup = Task.CompletedTask;
    private string? _lastResultFile;

    private sealed class PreviewSession(Uri? networkSource, WebVideoLink? webFormat)
    {
        public Uri? NetworkSource { get; } = networkSource;
        public WebVideoLink? WebFormat { get; } = webFormat;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Startup { get; set; } = Task.CompletedTask;
        public LivePreview? Relay { get; set; }
        public DirectoryInfo? AudioFiles { get; set; }
        public MediaSource? Source { get; set; }
        public MediaPlayer? Player { get; set; }
        public Action? StopTrackObservation { get; set; }
    }

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

        await OpenPreviewAsync($"网络预览 · {source.Scheme.ToUpperInvariant()}", source, async session =>
        {
            if (source.Scheme is "http" or "https")
                return MediaSource.CreateFromUri(source);

            session.Relay = await LivePreview.StartAsync(_media, source, session.Cancellation.Token);
            return MediaSource.CreateFromUri(session.Relay.Playlist);
        });
    }

    private Task PreviewWebAudioAsync(WebVideoLink audio) => OpenPreviewAsync(audio.Label, null, async session =>
    {
        PreviewMessage.Text = "正在下载并准备音频预览…";
        session.AudioFiles = Directory.CreateTempSubdirectory("LiveRecorder-Audio-");
        string path = await _webAnalyzer.DownloadAudioAsync(audio, session.AudioFiles.FullName, BundledFfmpeg, session.Cancellation.Token);
        StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask(session.Cancellation.Token);
        return MediaSource.CreateFromStorageFile(file);
    });

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

    private Task PlayLocalAsync(string file) => OpenPreviewAsync(Path.GetFileName(file), null, async session =>
    {
        StorageFile media = await StorageFile.GetFileFromPathAsync(file).AsTask(session.Cancellation.Token);
        return MediaSource.CreateFromStorageFile(media);
    });

    private async Task OpenPreviewAsync(string title, Uri? networkSource, Func<PreviewSession, Task<MediaSource>> loadSource)
    {
        if (_closeWhenIdle)
            return;

        Task previous = StopPreviewAsync();
        var session = new PreviewSession(networkSource, SelectedWebFormat);
        _preview = session;
        PreviewName.Text = title;
        PreviewMessage.Text = "正在打开媒体…";
        StopPreviewButton.Visibility = Visibility.Visible;
        session.Startup = LoadAndPlayAsync();
        try { await session.Startup; }
        catch (Exception ex)
        {
            if (_preview == session)
                await PreviewFailedAsync(session, ex is OperationCanceledException ? "连接超时，请检查媒体链接。" : ex.Message);
        }

        async Task LoadAndPlayAsync()
        {
            await previous;
            session.Cancellation.Token.ThrowIfCancellationRequested();
            session.Source = await loadSource(session);
            session.Cancellation.Token.ThrowIfCancellationRequested();

            var player = new MediaPlayer { AutoPlay = true };
            session.Player = player;
            var item = new MediaPlaybackItem(session.Source);
            if (session.NetworkSource is not null)
            {
                item.AudioTracksChanged += TracksChanged;
                item.VideoTracksChanged += TracksChanged;
                session.StopTrackObservation = () =>
                {
                    item.AudioTracksChanged -= TracksChanged;
                    item.VideoTracksChanged -= TracksChanged;
                };

                void TracksChanged(MediaPlaybackItem sender, Windows.Foundation.Collections.IVectorChangedEventArgs _) =>
                    DispatcherQueue.TryEnqueue(() => UpdateDetectedAudio(session, sender));
            }
            MediaPlaybackSession playback = player.PlaybackSession;
            playback.NaturalDurationChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_preview == session)
                    PreviewTransport.Duration = playback.NaturalDuration;
            });
            player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_preview != session)
                    return;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                PreviewPlayer.Visibility = Visibility.Visible;
                PreviewTransport.Duration = playback.NaturalDuration;
                UpdateDetectedAudio(session, item);
            });
            player.MediaFailed += (sender, args) =>
            {
                string message = args.ErrorMessage;
                DispatcherQueue.TryEnqueue(() => _ = PreviewFailedAsync(session, message));
            };
            PreviewPlayer.SetMediaPlayer(player);
            player.Source = item;

            if (session.Relay is { } relay)
                _ = ObserveRelayAsync(session, relay);
        }
    }

    private void UpdateDetectedAudio(PreviewSession session, MediaPlaybackItem item)
    {
        if (_preview != session || session.NetworkSource is not { } source)
            return;
        var current = (Source: source, Format: session.WebFormat);
        if (item.AudioTracks.Count > 0 && item.VideoTracks.Count == 0)
            _detectedAudio = current;
        else if (item.VideoTracks.Count > 0 && _detectedAudio == current)
            _detectedAudio = null;
        UpdateRecordButton();
    }

    private async Task ObserveRelayAsync(PreviewSession session, LivePreview relay)
    {
        try { await relay.Completion; }
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
        PreviewMessage.Text = string.IsNullOrWhiteSpace(message) ? "无法播放此媒体。" : $"无法播放此媒体。{Environment.NewLine}{message}";
        await cleanup;
    }

    private async void StopPreview_Click(object sender, RoutedEventArgs e) => await StopPreviewAsync();

    private Task ReleaseDatagramPreviewAsync(Uri? source = null) =>
        _preview?.NetworkSource is { Scheme: "rtp" or "udp" } active &&
        (source is null || active.Port == source.Port) ? StopPreviewAsync() : Task.CompletedTask;

    private Task StopPreviewAsync()
    {
        if (_preview is not { } session)
            return _previewCleanup;
        _preview = null;
        session.Cancellation.Cancel();
        PreviewPlayer.Visibility = Visibility.Collapsed;
        PreviewPlayer.SetMediaPlayer(null);
        session.StopTrackObservation?.Invoke();
        session.Player?.Dispose();
        PreviewTransport.Duration = TimeSpan.Zero;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewMessage.Text = PreviewIdleMessage;
        PreviewName.Text = "";
        StopPreviewButton.Visibility = Visibility.Collapsed;
        _previewCleanup = CleanupAsync(session);
        return _previewCleanup;
    }

    private static async Task CleanupAsync(PreviewSession session)
    {
        try { await session.Startup; }
        catch (Exception) { /* The preview request reports startup errors. */ }
        try
        {
            session.Source?.Dispose();
            if (session.Relay is not null)
                await session.Relay.DisposeAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally
        {
            if (session.AudioFiles is not null)
                DeleteWebAudioFiles(session.AudioFiles);
            session.Cancellation.Dispose();
        }
    }
}
