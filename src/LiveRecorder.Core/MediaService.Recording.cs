using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

public sealed record RecordingFolder(string Directory, int PartCount, bool Reencoded);
public sealed record RecordingResult(IReadOnlyList<RecordingFolder> Folders, string? Interruption);
public sealed record RecordingEvent(DateTimeOffset Time, bool Recovered, string Detail);

public sealed record RecordingReconnectSettings(int StallSeconds = 10, int RetrySeconds = 2, int MaxWaitMinutes = 3)
{
    public const int MaxSeconds = 3600;
    public const int MaxMinutes = 1440;

    internal RecordingReconnectSettings Normalize() => this with
    {
        StallSeconds = Math.Clamp(StallSeconds, 1, MaxSeconds),
        RetrySeconds = Math.Clamp(RetrySeconds, 1, MaxSeconds),
        MaxWaitMinutes = Math.Clamp(MaxWaitMinutes, 1, MaxMinutes)
    };
}

public sealed partial class MediaService
{
    public Task<RecordingResult> RecordAsync(Uri source, string directory, int partMegabytes, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null, bool? live = null, IProgress<RecordingEvent>? events = null,
        RecordingReconnectSettings? reconnect = null) =>
        RecordSessionAsync(NetworkInput(source), source, live, directory, partMegabytes, encoder, progress, token,
            deinterlace, settings, events, reconnect);

    public Task<RecordingResult> RecordSdpAsync(string path, string directory, int partMegabytes, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null, IProgress<RecordingEvent>? events = null, RecordingReconnectSettings? reconnect = null)
    {
        if (!Path.GetExtension(path).Equals(".sdp", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("请选择有效的本地 SDP 文件。");
        return RecordSessionAsync(["-protocol_whitelist", "file,udp,rtp", "-f", "sdp", "-i", Path.GetFullPath(path)],
            null, true, directory, partMegabytes, encoder, progress, token, deinterlace, settings, events, reconnect);
    }

    private async Task<RecordingResult> RecordSessionAsync(string[] input, Uri? source, bool? live, string directory,
        int partMegabytes, VideoEncoder encoder, IProgress<MediaProgress>? progress, CancellationToken token,
        DeinterlaceMode deinterlace, EncodingSettings? settings, IProgress<RecordingEvent>? events,
        RecordingReconnectSettings? reconnect)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partMegabytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partMegabytes, 1024);
        token.ThrowIfCancellationRequested();
        string executable = ToolProcess.RequireExecutable(ffmpeg, "FFmpeg");
        Directory.CreateDirectory(cacheDirectory);
        RecordingReconnectSettings timing = (reconnect ?? new()).Normalize();
        TimeSpan stall = TimeSpan.FromSeconds(timing.StallSeconds);
        TimeSpan retryDelay = TimeSpan.FromSeconds(timing.RetrySeconds);
        TimeSpan reconnectLimit = TimeSpan.FromMinutes(timing.MaxWaitMinutes);
        string expired = $"已达到 {timing.MaxWaitMinutes} 分钟的重连等待上限。";
        var folders = new List<RecordingFolder>();
        bool http = source?.Scheme is "http" or "https";
        bool hls = http && IsPlaylistPath(source!.AbsolutePath);
        bool liveHls = false;
        string? stalledPlaylist = null;
        long? outage = null;
        string? terminal = null;

        while (!token.IsCancellationRequested)
        {
            if (outage is long since)
            {
                TimeSpan remaining = reconnectLimit - Stopwatch.GetElapsedTime(since);
                if (remaining <= TimeSpan.Zero)
                {
                    terminal = expired;
                    break;
                }
                try { await Task.Delay(retryDelay < remaining ? retryDelay : remaining, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                remaining = reconnectLimit - Stopwatch.GetElapsedTime(since);
                if (remaining <= TimeSpan.Zero)
                {
                    terminal = expired;
                    break;
                }
                if (stalledPlaylist is not null)
                {
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
                    check.CancelAfter(remaining);
                    HttpMediaState? current = await InspectHttpMediaAsync(source!, check.Token).ConfigureAwait(false);
                    if (current?.HasFixedDuration == true)
                        break;
                    if (current?.LastHlsSegment is not { } latest || latest == stalledPlaylist)
                        continue;
                }
                if (token.IsCancellationRequested)
                    break;
            }

            // After reconnect, start close to the live edge instead of replaying the whole HLS window.
            string[] attemptInput = outage.HasValue && hls
                ? ["-live_start_index", "-1", .. (IsPlaylistPath(source!.AbsolutePath) ? [] : new[] { "-f", "hls" }), .. input]
                : input;
            Action<DateTimeOffset, string>? onRestored = outage.HasValue
                ? (at, folder) => events?.Report(new(at, true, $"已开始录制 {Path.GetFileName(folder)}。"))
                : null;
            TimeSpan startupWait = outage is long started
                ? reconnectLimit - Stopwatch.GetElapsedTime(started) : stall;
            if (startupWait <= TimeSpan.Zero)
            {
                terminal = expired;
                break;
            }
            RecordingAttempt attempt = await RecordSegmentAsync(executable, attemptInput, directory, partMegabytes, encoder,
                progress, token, deinterlace, settings ?? DefaultTranscodeSettings, onRestored,
                startupWait < stall ? startupWait : stall, stall).ConfigureAwait(false);
            hls |= attempt.Hls;
            liveHls |= attempt.Hls && attempt.DurationUnknown;
            if (attempt.Folder is { } recorded)
            {
                folders.Add(recorded);
                outage = null;
                stalledPlaylist = null;
            }
            if (token.IsCancellationRequested)
                break;

            bool expectLive = live == true || !http || liveHls || (live is null && attempt.DurationUnknown);
            bool recoverable = attempt.Stalled ||
                (attempt.Error is FfmpegFailureException failure && IsRecoverable(failure, folders.Count > 0, expectLive || hls));
            if (attempt.Error is { } error && !recoverable)
            {
                terminal = error.Message;
                break;
            }
            if (!recoverable && !expectLive)
                break;

            var (stoppedAt, stoppedTimestamp) = attempt.StallDetected ?? (DateTimeOffset.Now, Stopwatch.GetTimestamp());
            if (http && (hls || (attempt.Error is null && expectLive)))
            {
                HttpMediaState? state = await InspectHttpMediaAsync(source!, token).ConfigureAwait(false);
                if (state?.HasFixedDuration == true &&
                    ((liveHls && state.LastHlsSegment is not null) || (attempt.Error is null && !attempt.Stalled)))
                    break;
                hls |= state?.LastHlsSegment is not null;
                liveHls |= state is { HasFixedDuration: false, LastHlsSegment: not null };
                if (attempt.Folder is not null && state?.HasFixedDuration == false)
                    stalledPlaylist = state.LastHlsSegment;
            }
            if (outage is null && !token.IsCancellationRequested)
            {
                outage = stoppedTimestamp;
                string detail = attempt.Stalled ? $"{timing.StallSeconds} 秒未收到新的媒体数据。" :
                    attempt.Error is not null ? "媒体连接中断。" : "直播暂时停止提供媒体数据。";
                if (attempt.LastMediaAt is { } last)
                    detail += $" 最后收到媒体 {last:yyyy-MM-dd HH:mm:ss zzz}。";
                events?.Report(new(stoppedAt, false, detail));
            }
        }

        if (folders.Count == 0)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException(terminal ?? "录制结束，没有收到可用媒体数据。");
        }
        return new RecordingResult(folders.AsReadOnly(), terminal);
    }

    private async Task<RecordingAttempt> RecordSegmentAsync(string executable, string[] input, string directory, int partMegabytes,
        VideoEncoder encoder, IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace,
        EncodingSettings settings, Action<DateTimeOffset, string>? onRestored, TimeSpan startupWait, TimeSpan stall)
    {
        string? recordingDirectory = null;
        string[] codecs = ["-c", "copy"];
        var streams = new SourceEncodingInfo();
        bool convertVideo = false;
        bool convertAudio = false;
        bool hls = false;
        bool durationUnknown = false;
        bool readingDuration = false;
        bool stalled = false;
        (DateTimeOffset At, long Timestamp)? stallDetected = null;
        DateTimeOffset? lastMediaAt = null;
        Exception? error = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var detection = watch.Token.Register(() =>
                {
                    if (!token.IsCancellationRequested)
                        stallDetected = (DateTimeOffset.Now, Stopwatch.GetTimestamp());
                });
                watch.CancelAfter(startupWait);
                try
                {
                    await FfmpegProcess.RunAsync(executable, cacheDirectory,
                        ["-loglevel", "info", .. input, .. MapStreams(), .. codecs, "-f", "mpegts", "pipe:1"],
                        progress, watch.Token,
                        readOutput: stream => RecordingParts.SplitAsync(stream, BeginFolder, cacheDirectory,
                            partMegabytes * 1_000_000L, progress, () =>
                            {
                                lastMediaAt = DateTimeOffset.Now;
                                watch.CancelAfter(stall);
                            }),
                        finishOnCancellation: true, readDiagnostic: ReadRecordingDiagnostic).ConfigureAwait(false);
                    stalled = watch.IsCancellationRequested && !token.IsCancellationRequested;
                    break;
                }
                catch (RecordingMuxException ex)
                {
                    if (recordingDirectory is { } rejected)
                    {
                        foreach (FileInfo part in RecordingParts.Find(rejected))
                            part.Delete();
                        Directory.Delete(rejected);
                        recordingDirectory = null;
                    }
                    if (ex.Video ? convertVideo : convertAudio)
                        throw;
                    convertVideo |= ex.Video;
                    convertAudio |= !ex.Video;
                    EncodingSettings fallback = settings with
                    {
                        KeepVideoFormat = !convertVideo && settings.KeepVideoFormat,
                        RateMode = convertVideo && settings.KeepVideoBitrate && streams.VideoBitrate is null
                            ? VideoRateMode.Quality : settings.RateMode,
                        KeepAudioFormat = !convertAudio && settings.KeepAudioFormat,
                        KeepAudioBitrate = settings.KeepAudioBitrate && (!convertAudio || streams.AudioBitrate is not null)
                    };
                    codecs = EncodingArguments.Create(encoder, deinterlace, fallback, streams, forceVideo: convertVideo);
                    streams = new SourceEncodingInfo();
                }
                catch (OperationCanceledException) when (watch.IsCancellationRequested)
                {
                    stalled = !token.IsCancellationRequested;
                    break;
                }
            }
        }
        catch (Exception ex) { error = ex; }

        RecordingFolder? folder = null;
        if (recordingDirectory is { } path)
        {
            int count = RecordingParts.Find(path).Length;
            if (count > 0)
                folder = new RecordingFolder(path, count, convertVideo || convertAudio);
            else if (!Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        return new RecordingAttempt(folder, error, stalled, hls, durationUnknown, lastMediaAt, stalled ? stallDetected : null);

        string BeginFolder()
        {
            DateTimeOffset started = DateTimeOffset.Now;
            recordingDirectory = CreateRecordingDirectory(directory, started.LocalDateTime);
            onRestored?.Invoke(started, recordingDirectory);
            onRestored = null;
            return recordingDirectory;
        }

        void ReadRecordingDiagnostic(string line)
        {
            streams.Read(line);
            if (line.StartsWith("Input #0,", StringComparison.Ordinal))
            {
                readingDuration = true;
                hls |= line.StartsWith("Input #0, hls,", StringComparison.Ordinal);
            }
            else if (readingDuration && line.TrimStart().StartsWith("Duration:", StringComparison.Ordinal))
            {
                durationUnknown = line.Contains("Duration: N/A", StringComparison.Ordinal);
                readingDuration = false;
            }
            else if (line.StartsWith("Stream mapping:", StringComparison.Ordinal))
                readingDuration = false;

            if (!line.StartsWith("[mpegts @", StringComparison.Ordinal))
                return;
            if (line.Contains("is muxed as a private data stream", StringComparison.Ordinal))
                throw new RecordingMuxException(streams.HasVideo && line.Contains("] Stream 0,", StringComparison.Ordinal), line);
            if (line.Contains("AAC bitstream not in ADTS format and extradata missing", StringComparison.Ordinal))
                throw new RecordingMuxException(false, line);
        }
    }

    private static bool IsRecoverable(FfmpegFailureException error, bool hadMedia, bool live) =>
        error.Diagnostics.Any(line =>
            line.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Error number -138", StringComparison.Ordinal) ||
            line.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Failed to resolve hostname", StringComparison.OrdinalIgnoreCase) ||
            (line.Contains("Connection", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("reset", StringComparison.OrdinalIgnoreCase))) ||
            Regex.IsMatch(line, @"(?:HTTP error|Server returned) (?:408|429|5\d\d)\b", RegexOptions.IgnoreCase) ||
            (hadMedia && live && (Regex.IsMatch(line, @"(?:HTTP error|Server returned) 404\b", RegexOptions.IgnoreCase) ||
                line.Contains("End of file", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Input/output error", StringComparison.OrdinalIgnoreCase))));

    private sealed record RecordingAttempt(RecordingFolder? Folder, Exception? Error, bool Stalled, bool Hls,
        bool DurationUnknown, DateTimeOffset? LastMediaAt, (DateTimeOffset At, long Timestamp)? StallDetected);

    private sealed class RecordingMuxException(bool video, string message) : InvalidOperationException(message)
    {
        public bool Video { get; } = video;
    }
}
