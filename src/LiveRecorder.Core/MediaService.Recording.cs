using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

/// <summary>描述一次连续录制保存的分片目录。</summary>
/// <param name="Directory">分片目录的完整路径。</param>
/// <param name="PartCount">已保存的分片数。</param>
/// <param name="Reencoded">是否有原轨道需重新编码才能封装。</param>
public sealed record RecordingFolder(string Directory, int PartCount, bool Reencoded);
/// <summary>描述一次录制任务保存的结果。</summary>
/// <param name="Folders">按创建顺序排列的非空录制目录。</param>
/// <param name="Interruption">导致录制提前结束的原因。正常结束或手动停止时可为空。</param>
public sealed record RecordingResult(IReadOnlyList<RecordingFolder> Folders, string? Interruption);
/// <summary>描述一次断流等待或恢复录制事件。</summary>
/// <param name="Time">事件发生时间。</param>
/// <param name="Recovered">是否已经恢复录制。</param>
/// <param name="Detail">可显示或记录的事件详情。</param>
public sealed record RecordingEvent(DateTimeOffset Time, bool Recovered, string Detail);
/// <summary>集中定义录制分片、回退编码和重连选项。</summary>
/// <param name="PartMegabytes">单个录制分片的大小上限，单位为十进制 MB，范围为 1 至 1024。</param>
/// <param name="Encoder">原流无法直接封装且需要视频编码时使用的编码器。</param>
/// <param name="Deinterlace">视频重新编码时的去隔行模式。</param>
/// <param name="Encoding">可选的录制回退编码设置。</param>
/// <param name="Reconnect">可选的重连参数。</param>
public sealed record RecordingOptions(int PartMegabytes, VideoEncoder Encoder, DeinterlaceMode Deinterlace = DeinterlaceMode.Automatic,
    EncodingSettings? Encoding = null, RecordingReconnectSettings? Reconnect = null);

/// <summary>定义录制断流检测和重连的时间参数。</summary>
/// <param name="StallSeconds">连续无媒体数据多少秒后判定断流。</param>
/// <param name="RetrySeconds">尝试恢复的间隔秒数。</param>
/// <param name="MaxWaitMinutes">从检测到断流起允许等待恢复的分钟数。</param>
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

// 管理录制、媒体长时间无数据后的重连，以及每次恢复时独立保存分片。
public sealed partial class MediaService
{
    /// <summary>使用分开的录制设置参数从网络媒体源录制。</summary>
    /// <param name="source">媒体源地址。</param>
    /// <param name="directory">存放录制分片目录的父目录。</param>
    /// <param name="partMegabytes">单个录制分片的大小上限，单位为十进制 MB，范围为 1 至 1024。</param>
    /// <param name="encoder">原流无法直接封装且需要视频编码时使用的编码器。</param>
    /// <param name="progress">可选的任务进度接收方。</param>
    /// <param name="token">用于停止任务的取消令牌。</param>
    /// <param name="deinterlace">视频重新编码时的去隔行模式。</param>
    /// <param name="settings">可选的录制回退编码设置。</param>
    /// <param name="live">可选的直播标记。为空时由服务推断。</param>
    /// <param name="events">可选的断流和恢复事件接收方。</param>
    /// <param name="reconnect">可选的重连参数。</param>
    /// <param name="headers">可选的媒体请求头。</param>
    /// <returns>返回各录制目录和终止原因。取消时若已有有效分片，仍返回已录制结果。</returns>
    public Task<RecordingResult> RecordAsync(Uri source, string directory, int partMegabytes, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null, bool? live = null, IProgress<RecordingEvent>? events = null,
        RecordingReconnectSettings? reconnect = null, MediaRequestHeaders? headers = null) =>
        RecordAsync(source, directory, new RecordingOptions(partMegabytes, encoder, deinterlace, settings, reconnect),
            progress, token, live, events, headers);

    /// <summary>使用统一的录制选项从网络媒体源录制。</summary>
    /// <param name="source">媒体源地址。</param>
    /// <param name="directory">存放录制分片目录的父目录。</param>
    /// <param name="options">本次录制的分片、编码和重连设置。</param>
    /// <param name="progress">可选的任务进度接收方。</param>
    /// <param name="token">用于停止任务的取消令牌。</param>
    /// <param name="live">可选的直播标记。为空时由服务推断。</param>
    /// <param name="events">可选的断流和恢复事件接收方。</param>
    /// <param name="headers">可选的媒体请求头。</param>
    /// <returns>返回各录制目录和终止原因。取消时若已有有效分片，仍返回已录制结果。</returns>
    /// <exception cref="ArgumentOutOfRangeException">分片上限超出允许范围。</exception>
    /// <exception cref="OperationCanceledException">取消时尚未保存有效分片。</exception>
    public Task<RecordingResult> RecordAsync(Uri source, string directory, RecordingOptions options, IProgress<MediaProgress>? progress,
        CancellationToken token, bool? live = null, IProgress<RecordingEvent>? events = null, MediaRequestHeaders? headers = null) =>
        RecordSessionAsync(NetworkInput(source, headers: headers), source, live, directory, options, progress, token, events, headers);

    /// <summary>使用分开的录制设置参数，从本地 SDP 描述的 RTP 或 UDP 会话录制。</summary>
    /// <param name="path">有效的本地 SDP 文件路径。</param>
    /// <param name="directory">存放录制分片目录的父目录。</param>
    /// <param name="partMegabytes">单个录制分片的大小上限，单位为十进制 MB，范围为 1 至 1024。</param>
    /// <param name="encoder">原流无法直接封装且需要视频编码时使用的编码器。</param>
    /// <param name="progress">可选的任务进度接收方。</param>
    /// <param name="token">用于停止任务的取消令牌。</param>
    /// <param name="deinterlace">视频重新编码时的去隔行模式。</param>
    /// <param name="settings">可选的录制回退编码设置。</param>
    /// <param name="events">可选的断流和恢复事件接收方。</param>
    /// <param name="reconnect">可选的重连参数。</param>
    /// <returns>返回各录制目录和终止原因。取消时若已有有效分片，仍返回已录制结果。</returns>
    /// <exception cref="ArgumentException">文件不存在或扩展名不是 .sdp。</exception>
    public Task<RecordingResult> RecordSdpAsync(string path, string directory, int partMegabytes, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null, IProgress<RecordingEvent>? events = null, RecordingReconnectSettings? reconnect = null) =>
        RecordSdpAsync(path, directory, new RecordingOptions(partMegabytes, encoder, deinterlace, settings, reconnect),
            progress, token, events);

    /// <summary>从本地 SDP 描述的会话录制，并按照直播规则重连。</summary>
    /// <param name="path">有效的本地 SDP 文件路径。</param>
    /// <param name="directory">存放录制分片目录的父目录。</param>
    /// <param name="options">本次录制的分片、编码和重连设置。</param>
    /// <param name="progress">可选的任务进度接收方。</param>
    /// <param name="token">用于停止任务的取消令牌。</param>
    /// <param name="events">可选的断流和恢复事件接收方。</param>
    /// <returns>返回各录制目录和终止原因。取消时若已有有效分片，仍返回已录制结果。</returns>
    /// <exception cref="ArgumentException">文件不存在或扩展名不是 .sdp。</exception>
    /// <exception cref="ArgumentOutOfRangeException">分片上限超出允许范围。</exception>
    public Task<RecordingResult> RecordSdpAsync(string path, string directory, RecordingOptions options,
        IProgress<MediaProgress>? progress, CancellationToken token, IProgress<RecordingEvent>? events = null)
    {
        if (!Path.GetExtension(path).Equals(".sdp", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("请选择有效的本地 SDP 文件。");
        return RecordSessionAsync(["-protocol_whitelist", "file,udp,rtp", "-f", "sdp", "-i", Path.GetFullPath(path)],
            null, true, directory, options, progress, token, events, null);
    }

    private async Task<RecordingResult> RecordSessionAsync(string[] input, Uri? source, bool? live, string directory,
        RecordingOptions options, IProgress<MediaProgress>? progress, CancellationToken token, IProgress<RecordingEvent>? events,
        MediaRequestHeaders? headers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PartMegabytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PartMegabytes, 1024);
        token.ThrowIfCancellationRequested();
        string executable = ToolProcess.RequireExecutable(ffmpeg, "FFmpeg");
        Directory.CreateDirectory(cacheDirectory);
        RecordingReconnectSettings timing = (options.Reconnect ?? new()).Normalize();
        TimeSpan stall = TimeSpan.FromSeconds(timing.StallSeconds);
        TimeSpan retryDelay = TimeSpan.FromSeconds(timing.RetrySeconds);
        TimeSpan reconnectLimit = TimeSpan.FromMinutes(timing.MaxWaitMinutes);
        string expired = $"已达到 {timing.MaxWaitMinutes} 分钟的重连等待上限。";
        var folders = new List<RecordingFolder>();
        bool http = source?.Scheme is "http" or "https";
        bool hls = http && IsPlaylistPath(source!.AbsolutePath);
        bool liveHls = false;
        IReadOnlySet<Uri>? previousHlsRequests = null;
        long? previousHlsSequence = null;
        // 使用单调时钟计算等待上限，系统时间变动不会延长重连期限。
        long? outage = null;
        string? terminal = null;

        while (!token.IsCancellationRequested)
        {
            int hlsStart = -1;
            decimal? finalOffset = null;
            HttpMediaState? finalPlaylist = null;
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
                if (hls && previousHlsRequests is not null)
                {
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
                    check.CancelAfter(remaining);
                    HttpMediaState? current = await InspectHttpMediaAsync(source!, check.Token, headers).ConfigureAwait(false);
                    if (current?.HlsSegments is not { Count: > 0 } segments)
                        continue;
                    // 直播清单没有推进时继续等待。结束清单若还有新分片，则尝试保存尾段。
                    hlsStart = FindHlsResumeIndex(current, previousHlsRequests, previousHlsSequence);
                    if (hlsStart == segments.Count)
                    {
                        if (current.HasFixedDuration)
                            break;
                        continue;
                    }
                    if (current.HasFixedDuration)
                    {
                        finalPlaylist = current;
                        finalOffset = 0;
                        for (int i = 0; i < hlsStart; i++)
                            finalOffset += segments[i].Duration;
                        if (finalOffset is null)
                            finalOffset = 0; // Retain the tail even if the manifest cannot provide a seek offset.
                    }
                }
                if (token.IsCancellationRequested)
                    break;
            }

            // FFmpeg applies live_start_index only to live playlists. Final playlists use a time offset.
            string[] attemptInput = input;
            if (hls)
                attemptInput = ["-f", "hls", "-http_multiple", "0", .. input];
            if (outage.HasValue && hls)
            {
                string[] position = finalOffset is decimal offset
                    ? ["-ss", offset.ToString(CultureInfo.InvariantCulture)]
                    : ["-live_start_index", hlsStart.ToString(CultureInfo.InvariantCulture)];
                attemptInput = [.. position, .. attemptInput];
            }
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
            RecordingAttempt attempt;
            string? tailPlaylist = null;
            try
            {
                // 简单的结束清单可直接裁出尾段。其他清单保留原地址，交给 FFmpeg 按时间定位。
                if (hlsStart > 0 && finalPlaylist is { CanTrimHls: true } && headers is null)
                {
                    tailPlaylist = Path.Combine(cacheDirectory, $"hls-tail-{Guid.NewGuid():N}.m3u8");
                    await WriteHlsTailAsync(tailPlaylist, finalPlaylist, hlsStart, token).ConfigureAwait(false);
                    attemptInput = ["-f", "hls", "-http_multiple", "0", "-protocol_whitelist", "file,http,https,httpproxy,tcp,tls",
                        "-rw_timeout", DefaultTimeoutMicroseconds.ToString(CultureInfo.InvariantCulture), "-i", Path.GetFullPath(tailPlaylist)];
                }
                attempt = await RecordSegmentAsync(executable, attemptInput, directory, options, progress, token, onRestored,
                    startupWait < stall ? startupWait : stall, stall, http).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            finally
            {
                if (tailPlaylist is not null)
                    File.Delete(tailPlaylist);
            }
            hls |= attempt.Hls;
            liveHls |= attempt.Hls && attempt.DurationUnknown;
            if (attempt.Folder is { } recorded)
            {
                folders.Add(recorded);
                // 有分片写出后才视为本轮恢复，后续断流重新计时。
                outage = null;
                if (attempt.HlsRequests.Count > 0)
                {
                    previousHlsRequests = attempt.HlsRequests;
                    previousHlsSequence = null;
                }
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
            if (finalOffset.HasValue && attempt.Folder is not null && !recoverable)
                break;

            var (stoppedAt, stoppedTimestamp) = attempt.StallDetected ?? (DateTimeOffset.Now, Stopwatch.GetTimestamp());
            if (http && (hls || (attempt.Error is null && expectLive)))
            {
                HttpMediaState? state = await InspectHttpMediaAsync(source!, token, headers).ConfigureAwait(false);
                if (state?.HlsSegments is { Count: > 0 } segments)
                {
                    hls = true;
                    liveHls |= !state.HasFixedDuration;
                    if (attempt.Folder is not null && previousHlsRequests is not null)
                    {
                        int matched = FindHlsResumeIndex(state, previousHlsRequests, null);
                        if (matched > 0)
                            previousHlsSequence = state.HlsSequence + matched - 1;
                        if (state.HasFixedDuration && matched == segments.Count)
                            break;
                    }
                    else if (state.HasFixedDuration && !recoverable)
                        break;
                }
                else if (state?.HasFixedDuration == true && !recoverable)
                    break;
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

    // 优先用 FFmpeg 最近请求过的分片 URI 定位。旧分片滑出窗口后再使用序号。
    private static int FindHlsResumeIndex(HttpMediaState state, IReadOnlySet<Uri> previousRequests, long? previousSequence)
    {
        IReadOnlyList<HlsSegment> segments = state.HlsSegments!;
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            if (previousRequests.Contains(segments[i].Uri))
                return i + 1;
        }
        return previousSequence is long sequence
            ? (int)Math.Clamp(sequence - state.HlsSequence + 1, 0, segments.Count)
            : 0;
    }

    private static Task WriteHlsTailAsync(string path, HttpMediaState state, int start, CancellationToken token)
    {
        HlsSegment[] tail = state.HlsSegments!.Skip(start).ToArray();
        decimal target = Math.Ceiling(tail.Max(segment => segment.Duration!.Value));
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n");
        playlist.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{target}\n#EXT-X-MEDIA-SEQUENCE:{state.HlsSequence + start}\n");
        foreach (HlsSegment segment in tail)
            playlist.Append(CultureInfo.InvariantCulture, $"#EXTINF:{segment.Duration},\n{segment.Uri.AbsoluteUri}\n");
        playlist.Append("#EXT-X-ENDLIST\n");
        return File.WriteAllTextAsync(path, playlist.ToString(), token);
    }

    private async Task<RecordingAttempt> RecordSegmentAsync(string executable, string[] input, string directory, RecordingOptions options,
        IProgress<MediaProgress>? progress, CancellationToken token, Action<DateTimeOffset, string>? onRestored,
        TimeSpan startupWait, TimeSpan stall, bool trackHls)
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
        var requestedSegments = new LinkedList<Uri>();
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
                // 首次读到输出前使用启动期限，之后每次收到媒体数据都会重置无数据计时。
                watch.CancelAfter(startupWait);
                try
                {
                    await FfmpegProcess.RunAsync(executable, cacheDirectory,
                        ["-loglevel", trackHls ? "verbose" : "info", .. input, .. MapStreams(), .. codecs, "-f", "mpegts", "pipe:1"],
                        progress, watch.Token,
                        readOutput: stream => RecordingParts.SplitAsync(stream, BeginFolder, cacheDirectory,
                            options.PartMegabytes * 1_000_000L, progress, () =>
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
                    // MPEG-TS 无法直接封装某条轨道时，用编码设置重试该轨道。
                    EncodingSettings settings = options.Encoding ?? DefaultTranscodeSettings;
                    EncodingSettings fallback = settings with
                    {
                        KeepVideoFormat = !convertVideo && settings.KeepVideoFormat,
                        RateMode = convertVideo && settings.KeepVideoBitrate && streams.VideoBitrate is null
                            ? VideoRateMode.Quality : settings.RateMode,
                        KeepAudioFormat = !convertAudio && settings.KeepAudioFormat,
                        KeepAudioBitrate = settings.KeepAudioBitrate && (!convertAudio || streams.AudioBitrate is not null)
                    };
                    codecs = EncodingArguments.Create(options.Encoder, options.Deinterlace, fallback, streams, forceVideo: convertVideo);
                    streams = new SourceEncodingInfo();
                    requestedSegments.Clear();
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
        return new RecordingAttempt(folder, error, stalled, hls, durationUnknown, lastMediaAt, stalled ? stallDetected : null,
            requestedSegments.ToHashSet());

        // 首次写入有效分片时才创建目录，连接失败的尝试不会留下空目录。
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
            const string segmentMarker = "HLS request for url '";
            int marker = line.IndexOf(segmentMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                int start = marker + segmentMarker.Length;
                int end = line.LastIndexOf("', offset ", StringComparison.Ordinal);
                if (end > start && Uri.TryCreate(line[start..end], UriKind.Absolute, out Uri? segment))
                {
                    if (requestedSegments.Count == 128)
                        requestedSegments.RemoveFirst();
                    requestedSegments.AddLast(segment);
                }
            }
            else if (line.Contains("Failed to open segment", StringComparison.Ordinal) && requestedSegments.Count > 0)
            {
                requestedSegments.RemoveLast();
            }
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
        bool DurationUnknown, DateTimeOffset? LastMediaAt, (DateTimeOffset At, long Timestamp)? StallDetected,
        IReadOnlySet<Uri> HlsRequests);

    private sealed class RecordingMuxException(bool video, string message) : InvalidOperationException(message)
    {
        public bool Video { get; } = video;
    }
}
