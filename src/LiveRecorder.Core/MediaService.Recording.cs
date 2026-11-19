using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

/// <summary>描述一次连续录制保存的分片目录。</summary>
/// <param name="Directory">分片目录的完整路径。</param>
/// <param name="PartCount">已保存的分片数。</param>
/// <param name="VideoReencoded">是否对视频轨道重新编码。</param>
/// <param name="AudioReencoded">是否对音频轨道重新编码。</param>
/// <param name="SourceVideo">录制实际读取的视频编码、分辨率和源声明码率，纯音频时为空。</param>
public sealed record RecordingFolder(string Directory, int PartCount, bool VideoReencoded, bool AudioReencoded, string? SourceVideo = null)
{
    /// <summary>是否有原轨道需重新编码才能封装。</summary>
    public bool Reencoded => VideoReencoded || AudioReencoded;
}
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
/// <param name="PartDurationSeconds">FFmpeg 原生分片的目标时长，单位为秒。</param>
/// <param name="Encoder">原流无法直接封装且需要视频编码时使用的编码器。</param>
/// <param name="Deinterlace">视频重新编码时的去隔行模式。</param>
/// <param name="Encoding">可选的录制回退编码设置。</param>
/// <param name="Reconnect">可选的重连参数。</param>
/// <param name="ReportStreamInfo">是否报告输出轨道与分轨包计数，用于实时录制面板。</param>
public sealed record RecordingOptions(int PartDurationSeconds, VideoEncoder Encoder, DeinterlaceMode Deinterlace = DeinterlaceMode.Automatic,
    EncodingSettings? Encoding = null, RecordingReconnectSettings? Reconnect = null, bool ReportStreamInfo = false)
{
    /// <summary>旧配置或新安装默认使用的目标分片秒数。</summary>
    public const int DefaultPartDurationSeconds = 10;
}

/// <summary>定义录制断流检测和重连的时间参数。</summary>
/// <param name="StallSeconds">连续无媒体数据的基础断流阈值，单位为秒。HLS 直播还会考虑分片发布间隔。</param>
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
    /// <param name="partDurationSeconds">FFmpeg 原生分片的目标时长，单位为秒。</param>
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
    public Task<RecordingResult> RecordAsync(Uri source, string directory, int partDurationSeconds, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null, bool? live = null, IProgress<RecordingEvent>? events = null,
        RecordingReconnectSettings? reconnect = null, MediaRequestHeaders? headers = null) =>
        RecordAsync(source, directory, new RecordingOptions(partDurationSeconds, encoder, deinterlace, settings, reconnect),
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
    /// <param name="selection">同一媒体源已经选定的网络入口。</param>
    /// <returns>返回各录制目录和终止原因。取消时若已有有效分片，仍返回已录制结果。</returns>
    /// <exception cref="ArgumentOutOfRangeException">分片目标时长小于一秒。</exception>
    /// <exception cref="OperationCanceledException">取消时尚未保存有效分片。</exception>
    public Task<RecordingResult> RecordAsync(Uri source, string directory, RecordingOptions options, IProgress<MediaProgress>? progress,
        CancellationToken token, bool? live = null, IProgress<RecordingEvent>? events = null, MediaRequestHeaders? headers = null,
        NetworkMediaSelection? selection = null) =>
        RecordSessionAsync(NetworkInput(selection?.Source ?? source, headers: headers), source, live, directory,
            options, progress, token, events, headers, selection);

    /// <summary>使用分开的录制设置参数，从本地 SDP 描述的 RTP 或 UDP 会话录制。</summary>
    /// <param name="path">有效的本地 SDP 文件路径。</param>
    /// <param name="directory">存放录制分片目录的父目录。</param>
    /// <param name="partDurationSeconds">FFmpeg 原生分片的目标时长，单位为秒。</param>
    /// <param name="encoder">原流无法直接封装且需要视频编码时使用的编码器。</param>
    /// <param name="progress">可选的任务进度接收方。</param>
    /// <param name="token">用于停止任务的取消令牌。</param>
    /// <param name="deinterlace">视频重新编码时的去隔行模式。</param>
    /// <param name="settings">可选的录制回退编码设置。</param>
    /// <param name="events">可选的断流和恢复事件接收方。</param>
    /// <param name="reconnect">可选的重连参数。</param>
    /// <returns>返回各录制目录和终止原因。取消时若已有有效分片，仍返回已录制结果。</returns>
    /// <exception cref="ArgumentException">文件不存在或扩展名不是 .sdp。</exception>
    public Task<RecordingResult> RecordSdpAsync(string path, string directory, int partDurationSeconds, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null, IProgress<RecordingEvent>? events = null, RecordingReconnectSettings? reconnect = null) =>
        RecordSdpAsync(path, directory, new RecordingOptions(partDurationSeconds, encoder, deinterlace, settings, reconnect),
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
    /// <exception cref="ArgumentOutOfRangeException">分片目标时长小于一秒。</exception>
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
        MediaRequestHeaders? headers, NetworkMediaSelection? selection = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PartDurationSeconds, 1);
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
        Uri? playlistSource = selection?.VariantPlaylist ?? selection?.Source ?? source;
        int? program = selection?.Program;
        HttpMediaState? initialState = http
            ? await InspectHttpMediaAsync(playlistSource!, token, headers).ConfigureAwait(false) : null;
        if (selection?.IsHls != true)
            ApplyVariant(initialState);
        bool hls = http && (selection?.IsHls == true || initialState?.HlsSegments is not null || IsPlaylistPath(source!.AbsolutePath));
        bool liveHls = initialState is { HasFixedDuration: false, HlsSegments: not null };
        bool paceRecording = http && (initialState?.HasFixedDuration == true && (hls || live != true) ||
            initialState is null && live == false);
        TimeSpan mediaStall = RecordingStallThreshold(stall, initialState);
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
                if (http && (initialState is null || hls && previousHlsRequests is not null))
                {
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
                    check.CancelAfter(remaining);
                    HttpMediaState? current = await InspectHttpMediaAsync(playlistSource!, check.Token, headers).ConfigureAwait(false);
                    if (initialState is null && current is not null)
                    {
                        if (selection?.IsHls != true)
                            ApplyVariant(current);
                        initialState = current;
                        hls |= current.HlsSegments is not null;
                        liveHls |= hls && !current.HasFixedDuration;
                        paceRecording = current.HasFixedDuration && (hls || live != true);
                        mediaStall = RecordingStallThreshold(stall, current);
                    }
                    if (hls && previousHlsRequests is not null)
                    {
                        if (current?.HlsSegments is not { Count: > 0 } segments)
                            continue;
                        mediaStall = RecordingStallThreshold(stall, current);
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
                }
                if (token.IsCancellationRequested)
                    break;
            }

            string[] attemptInput = paceRecording ? ["-re", .. input] : input;
            if (hls)
            {
                // FFmpeg 仅在直播清单使用分片索引。首次从最新分片开始，重连时保留尚未录制的区间。
                string[] position = finalOffset is decimal offset
                    ? ["-ss", offset.ToString(CultureInfo.InvariantCulture)]
                    : ["-live_start_index", hlsStart.ToString(CultureInfo.InvariantCulture)];
                attemptInput = [.. position, "-f", "hls", "-http_multiple", "0", .. attemptInput];
            }
            Action<DateTimeOffset, string>? onRestored = outage.HasValue
                ? (at, folder) => events?.Report(new(at, true, $"已开始录制 {Path.GetFileName(folder)}。"))
                : null;
            TimeSpan attemptStall = mediaStall;
            TimeSpan startupWait = outage is long started
                ? reconnectLimit - Stopwatch.GetElapsedTime(started) : attemptStall;
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
                if (hlsStart > 0 && finalPlaylist is { CanTrimHls: true } && headers is null && program is null)
                {
                    tailPlaylist = Path.Combine(cacheDirectory, $"hls-tail-{Guid.NewGuid():N}.m3u8");
                    await WriteHlsTailAsync(tailPlaylist, finalPlaylist, hlsStart, token).ConfigureAwait(false);
                    attemptInput = ["-f", "hls", "-http_multiple", "0", "-protocol_whitelist", "file,http,https,httpproxy,tcp,tls",
                        "-rw_timeout", DefaultTimeoutMicroseconds.ToString(CultureInfo.InvariantCulture), "-i", Path.GetFullPath(tailPlaylist)];
                }
                attempt = await RecordSegmentAsync(executable, attemptInput, directory, options, progress, token, onRestored,
                    startupWait < attemptStall ? startupWait : attemptStall, attemptStall, http, program).ConfigureAwait(false);
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
            {
                terminal = attempt.Error?.Message;
                break;
            }

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
                HttpMediaState? state = await InspectHttpMediaAsync(playlistSource!, token, headers).ConfigureAwait(false);
                if (state?.HlsSegments is { Count: > 0 } segments)
                {
                    hls = true;
                    liveHls |= !state.HasFixedDuration;
                    mediaStall = RecordingStallThreshold(stall, state);
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
                string detail = attempt.Stalled ? $"{attemptStall.TotalSeconds:0} 秒未收到新的媒体数据。" :
                    attempt.Error is not null ? "媒体连接中断。" : "直播暂时停止提供媒体数据。";
                if (attempt.LastMediaAt is { } last)
                    detail += $" 最后收到媒体 {last:yyyy-MM-dd HH:mm:ss zzz}。";
                events?.Report(new(stoppedAt, false, detail));
            }
        }

        if (folders.Count == 0)
        {
            if (terminal is null)
                token.ThrowIfCancellationRequested();
            throw new InvalidOperationException(terminal ?? "录制结束，没有收到可用媒体数据。");
        }
        return new RecordingResult(folders.AsReadOnly(), terminal);

        // 一次录制固定使用开始时选中的清晰度，重连只重新读取该变体的分片清单。
        void ApplyVariant(HttpMediaState? state)
        {
            if (state?.VariantSource is not { } variant)
                return;
            input = NetworkInput(variant, headers: headers);
            playlistSource = state.VariantPlaylist;
            program = state.Program;
        }
    }

    /// <summary>根据直播清单的目标分片时长，为发布与轮询抖动留出等待时间。</summary>
    /// <param name="configured">用户设置的基础断流阈值。</param>
    /// <param name="state">从 HTTP 媒体源探测到的清单状态。</param>
    /// <returns>HLS 直播采用基础阈值与两倍目标分片时长中的较大值，其他输入使用基础阈值。</returns>
    private static TimeSpan RecordingStallThreshold(TimeSpan configured, HttpMediaState? state) =>
        state is { HasFixedDuration: false, HlsTargetDurationSeconds: > 0 } && 2d * state.HlsTargetDurationSeconds > configured.TotalSeconds
            ? TimeSpan.FromSeconds(Math.Min(2d * state.HlsTargetDurationSeconds, RecordingReconnectSettings.MaxSeconds))
            : configured;

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
        TimeSpan startupWait, TimeSpan stall, bool trackHls, int? program)
    {
        string? recordingDirectory = null;
        string[] codecs = ["-c", "copy"];
        string extension = RecordingParts.TransportExtension;
        var streams = new SourceEncodingInfo(program);
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
        RecordingStatistics? statistics = null;
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
                string staging = Directory.CreateDirectory(Path.Combine(cacheDirectory, $"record-native-{Guid.NewGuid():N}")).FullName;
                try
                {
                    bool matroska = extension == RecordingParts.MatroskaExtension;
                    statistics = options.ReportStreamInfo && progress is not null
                        ? new RecordingStatistics(extension, progress, convertVideo, convertAudio) : null;
                    statistics?.Start();
                    // TS 连续读入合并。Matroska 分片独立封装，时间戳从各自起点开始。
                    string pattern = Path.Combine(staging.Replace("%", "%%", StringComparison.Ordinal), "part-%08d" + extension);
                    watch.CancelAfter(startupWait);
                    Task recording = FfmpegProcess.RunAsync(executable, cacheDirectory,
                        ["-loglevel", trackHls || statistics is not null ? "verbose" : "info", .. input, .. MapStreams(program), .. codecs,
                            "-f", "segment", "-segment_format", matroska ? "matroska" : "mpegts",
                            "-segment_format_options", matroska ? "flush_packets=1:cluster_time_limit=1000" : "flush_packets=1:mpegts_flags=+initial_discontinuity",
                            "-segment_time", options.PartDurationSeconds.ToString(CultureInfo.InvariantCulture),
                            "-segment_start_number", "1", "-reset_timestamps", matroska ? "1" : "0", pattern],
                        statistics ?? progress, watch.Token,
                        finishOnCancellation: true, readDiagnostic: ReadRecordingDiagnostic, traceTimestamps: statistics is not null);
                    await RecordingParts.PublishAsync(recording, staging, extension, BeginFolder, watch, progress, () =>
                    {
                        if (!watch.IsCancellationRequested)
                        {
                            lastMediaAt = DateTimeOffset.Now;
                            watch.CancelAfter(stall);
                        }
                    }).ConfigureAwait(false);
                    stalled = watch.IsCancellationRequested && !token.IsCancellationRequested;
                    break;
                }
                catch (RecordingMuxException ex)
                {
                    foreach (string part in Directory.EnumerateFiles(staging, "part-*" + extension))
                        File.Delete(part);
                    if (recordingDirectory is { } rejected)
                    {
                        if (RecordingParts.Find(rejected).Length > 0)
                            throw;
                        Directory.Delete(rejected);
                        recordingDirectory = null;
                    }
                    if (extension == RecordingParts.TransportExtension)
                    {
                        // TS 拒绝任一轨道时，先让 Matroska 尝试复制全部已选轨道。
                        extension = RecordingParts.MatroskaExtension;
                    }
                    else
                    {
                        if (ex.Video ? convertVideo : convertAudio)
                            throw new InvalidOperationException($"按当前编码设置生成的{(ex.Video ? "视频" : "音频")}仍无法封装。请检查「编码设置」。{Environment.NewLine}{ex.Message}", ex);
                        convertVideo |= ex.Video;
                        convertAudio |= !ex.Video;
                        codecs = EncodingArguments.CreateRecordingFallback(options.Encoder, options.Deinterlace,
                            options.Encoding ?? DefaultTranscodeSettings, streams, convertVideo, convertAudio);
                    }
                    streams = new SourceEncodingInfo(program);
                    requestedSegments.Clear();
                }
                catch (OperationCanceledException) when (watch.IsCancellationRequested)
                {
                    stalled = !token.IsCancellationRequested;
                    break;
                }
                finally
                {
                    if (!Directory.EnumerateFileSystemEntries(staging).Any())
                        Directory.Delete(staging);
                }
            }
        }
        catch (Exception ex) { error = ex; }

        RecordingFolder? folder = null;
        if (recordingDirectory is { } path)
        {
            int count = RecordingParts.Find(path).Length;
            if (count > 0)
            {
                string? video = streams.HasVideo ? string.Join(" · ", new[]
                {
                    streams.VideoCodec,
                    streams.VideoResolution,
                    streams.VideoBitrate is long bits ? $"{bits / 1000d:0.##} kb/s" : null
                }.OfType<string>()) : null;
                folder = new RecordingFolder(path, count, convertVideo, convertAudio, video);
            }
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
            statistics?.Read(line);
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

            if (extension == RecordingParts.TransportExtension && line.StartsWith("[mpegts @", StringComparison.Ordinal))
            {
                if (line.Contains("is muxed as a private data stream", StringComparison.Ordinal))
                    throw new RecordingMuxException(streams.HasVideo && line.Contains("] Stream 0,", StringComparison.Ordinal), line);
                if (line.Contains("AAC bitstream not in ADTS format and extradata missing", StringComparison.Ordinal))
                    throw new RecordingMuxException(false, line);
            }
            else if (extension == RecordingParts.MatroskaExtension && line.StartsWith("[matroska @", StringComparison.Ordinal))
            {
                if (line.Contains("No bmp codec tag found", StringComparison.Ordinal) ||
                    line.Contains("Raw RGB is not supported Natively in Matroska", StringComparison.Ordinal))
                    throw new RecordingMuxException(true, line);
                if (line.Contains("No wav codec tag found", StringComparison.Ordinal) ||
                    line.Contains("Error parsing AAC extradata", StringComparison.Ordinal) ||
                    line.Contains("ALAC expects a 36-byte", StringComparison.Ordinal))
                    throw new RecordingMuxException(false, line);
            }
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
