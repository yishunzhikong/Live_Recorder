using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace LiveRecorder.Core;

public enum VideoEncoder { Software, Intel, Nvidia, Amd }
public enum DeinterlaceMode { Automatic, Always, Off }

/// <summary>表示媒体任务的一次增量进度更新。</summary>
/// <param name="Bytes">累计处理的字节数。</param>
/// <param name="TotalBytes">已知的总字节数。</param>
/// <param name="Parts">已保存的分片数。</param>
/// <param name="MediaTime">已处理的媒体时长。</param>
/// <param name="Recording">录制输出参数和分轨包计数。</param>
/// <remarks>空字段表示此次更新未提供对应的指标。</remarks>
public sealed record MediaProgress(long? Bytes = null, long? TotalBytes = null, int? Parts = null, TimeSpan? MediaTime = null,
    RecordingMediaProgress? Recording = null);
/// <summary>提供媒体探测、下载、录制、预览输入和本地编辑。</summary>
/// <param name="ffmpeg">选用的 FFmpeg 路径。需要 FFmpeg 的操作会检查该路径。</param>
/// <param name="cacheDirectory">便携缓存目录，用于未发布的媒体输出。</param>
public sealed partial class MediaService(string? ffmpeg, string cacheDirectory)
{
    private const int CopyBuffer = 128 * 1024;
    private const long DefaultTimeoutMicroseconds = 15_000_000;
    private const string NetworkProtocols = "http,https,httpproxy,tcp,tls,udp,rtp,rtmp,rtmps,rtmpt,rtmpts,srt,crypto";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AllowAutoRedirect = true
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>验证并解析受支持的绝对媒体地址。</summary>
    /// <param name="text">用户输入的地址，可包含首尾空白。</param>
    /// <returns>已验证的媒体 URI。</returns>
    /// <exception cref="ArgumentException">地址缺少主机、格式无效或使用不受支持的协议。</exception>
    public static Uri ParseSource(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Scheme is not ("http" or "https" or "rtp" or "udp" or "rtmp" or "rtmps" or "rtmpt" or "rtmpts" or "rtsp" or "rtsps" or "srt"))
            throw new ArgumentException("请输入 HTTP(S)、RTSP(S)、RTMP(S)、RTP、UDP 或 SRT 媒体链接。");
        return uri;
    }

    /// <summary>尝试确认 HTTP 媒体是否具有固定长度。</summary>
    /// <param name="source">待探测的媒体地址。</param>
    /// <param name="token">用于中止探测的取消令牌。</param>
    /// <param name="headers">可选的媒体请求头。</param>
    /// <returns>能确认固定长度时返回 <see langword="true" />。无法确认或探测失败时返回 <see langword="false" />。</returns>
    public static async Task<bool> HasFixedPreviewDurationAsync(Uri source, CancellationToken token, MediaRequestHeaders? headers = null) =>
        (await InspectHttpMediaAsync(source, token, headers).ConfigureAwait(false))?.HasFixedDuration == true;

    /// <summary>预览、录制和下载使用的网络媒体入口。</summary>
    /// <param name="Source">实际交给播放器或 FFmpeg 的地址。</param>
    /// <param name="IsHls">是否已识别为 HLS。</param>
    /// <param name="Program">保留 HLS 主清单时选用的 FFmpeg 节目编号。</param>
    /// <param name="HasFixedDuration">是否确认媒体已经具有固定时长。</param>
    /// <param name="VariantPlaylist">选中变体的分片清单，供录制重连使用。</param>
    public sealed record NetworkMediaSelection(Uri Source, bool IsHls, int? Program, bool HasFixedDuration, Uri? VariantPlaylist = null);

    /// <summary>解析 HLS 清晰度并确认媒体长度，供网络预览使用。</summary>
    /// <param name="source">原始媒体地址。</param>
    /// <param name="token">用于中止探测的令牌。</param>
    /// <param name="headers">可选的媒体请求头。</param>
    /// <returns>选中的入口。探测失败时保留原始地址。</returns>
    public static async Task<NetworkMediaSelection> SelectNetworkMediaAsync(Uri source, CancellationToken token,
        MediaRequestHeaders? headers = null)
    {
        HttpMediaState? state = await InspectHttpMediaAsync(source, token, headers).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new(state?.VariantSource ?? source, state?.HlsSegments is not null, state?.Program,
            state?.HasFixedDuration == true, state?.VariantPlaylist);
    }

    // 同一份 HTTP 探测结果用于判断预览长度和定位录制重连后的 HLS 分片。
    private sealed record HlsSegment(Uri Uri, decimal? Duration);
    private sealed record HttpMediaState(bool HasFixedDuration, long HlsSequence = 0, IReadOnlyList<HlsSegment>? HlsSegments = null,
        bool CanTrimHls = false, int HlsTargetDurationSeconds = 0, Uri? VariantSource = null, Uri? VariantPlaylist = null,
        int? Program = null);

    private sealed record HlsVariant(Uri Playlist, long Pixels, long Bandwidth, string? AudioGroup, string? VideoGroup, int Program);
    private static readonly Regex HlsAttribute = new(@"(?:^|,)\s*([A-Z0-9-]+)=(?:""([^""]*)""|([^,]*))", RegexOptions.CultureInvariant);

    private static async Task<HttpMediaState?> InspectHttpMediaAsync(Uri source, CancellationToken token, MediaRequestHeaders? headers)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await ReadHttpMediaAsync(source, 0, timeout.Token, headers).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or XmlException)
        {
            return null;
        }
    }

    private static async Task<HttpMediaState?> ReadHttpMediaAsync(Uri source, int depth, CancellationToken token, MediaRequestHeaders? headers)
    {
        if (depth > 3 || source.Scheme is not ("http" or "https"))
            return null;
        using var response = await GetHttpMediaAsync(source, token, headers).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        Uri location = response.RequestMessage?.RequestUri ?? source;
        string? mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] prefix = new byte[512];
        int count = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken: token)
            .ConfigureAwait(false);
        string head = Encoding.UTF8.GetString(prefix, 0, count).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        bool hls = head.StartsWith("#EXTM3U", StringComparison.Ordinal) || IsPlaylistPath(location.AbsolutePath) ||
            mime is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl" or "audio/x-mpegurl";
        if (!hls && !head.StartsWith('<') && mime != "application/dash+xml" &&
            !location.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return new(count > 0 && (response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength) > 0);
        }

        // Inspect a bounded manifest so an indeterminate response cannot consume playback resources.
        byte[] manifest = new byte[hls ? 1024 * 1024 : 8192];
        prefix.AsSpan(0, count).CopyTo(manifest);
        count += await stream.ReadAtLeastAsync(manifest.AsMemory(count), manifest.Length - count,
            throwOnEndOfStream: false, cancellationToken: token).ConfigureAwait(false);
        if (!hls)
        {
            using var input = new MemoryStream(manifest, 0, count);
            using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            return new(xml.MoveToContent() == XmlNodeType.Element && xml.LocalName == "MPD" &&
                xml.GetAttribute("type") is null or "static");
        }

        using var lines = new StringReader(Encoding.UTF8.GetString(manifest, 0, count));
        var variants = new List<HlsVariant>();
        var externalGroups = new HashSet<(string Type, string Group)>();
        string? variantAttributes = null;
        bool ended = false;
        bool canTrim = true;
        long sequence = 0;
        int targetDuration = 0;
        decimal? duration = null;
        var segments = new List<HlsSegment>();
        while (lines.ReadLine()?.Trim() is { } line)
        {
            if (line == "#EXT-X-ENDLIST")
                ended = true;
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                long.TryParse(line.AsSpan("#EXT-X-MEDIA-SEQUENCE:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                int.TryParse(line.AsSpan("#EXT-X-TARGETDURATION:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out targetDuration);
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> value = line.AsSpan("#EXTINF:".Length);
                int comma = value.IndexOf(',');
                duration = decimal.TryParse(comma < 0 ? value : value[..comma], NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal seconds) ? seconds : null;
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                variantAttributes = line["#EXT-X-STREAM-INF:".Length..];
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var attributes = ReadHlsAttributes(line["#EXT-X-MEDIA:".Length..]);
                if (attributes.TryGetValue("TYPE", out string? type) && type is "AUDIO" or "VIDEO" &&
                    attributes.TryGetValue("GROUP-ID", out string? group) && attributes.ContainsKey("URI"))
                    externalGroups.Add((type, group));
            }
            else if (variantAttributes is not null && line.Length != 0 && !line.StartsWith('#'))
            {
                if (Uri.TryCreate(location, line, out Uri? playlist) && playlist.Scheme is "http" or "https")
                    variants.Add(ReadVariant(playlist, variantAttributes, variants.Count));
                variantAttributes = null;
            }
            else if (line.Length != 0 && !line.StartsWith('#'))
            {
                if (Uri.TryCreate(location, line, out Uri? segment))
                    segments.Add(new(segment, duration));
                duration = null;
            }
            else if (line.StartsWith("#EXT-X-", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal) &&
                line != "#EXT-X-INDEPENDENT-SEGMENTS")
                canTrim = false;
        }
        // 主播放列表只有变体入口，需要继续读取变体清单才能判断是否结束。
        if (variants.Count > 0)
        {
            HlsVariant selected = variants.OrderByDescending(variant => variant.Pixels)
                .ThenByDescending(variant => variant.Bandwidth).First();
            HttpMediaState child = await ReadHttpMediaAsync(selected.Playlist, depth + 1, token, headers).ConfigureAwait(false)
                ?? new(false, HlsSegments: []);
            // 有独立轨道组的变体需要保留主清单，以便 FFmpeg 同时读取视频和音频。
            if (selected.AudioGroup is { } audio && externalGroups.Contains(("AUDIO", audio)) ||
                selected.VideoGroup is { } video && externalGroups.Contains(("VIDEO", video)))
                return child with { VariantSource = source, VariantPlaylist = selected.Playlist, Program = selected.Program, CanTrimHls = false };
            return child with { VariantSource = child.VariantSource ?? selected.Playlist, VariantPlaylist = child.VariantPlaylist ?? selected.Playlist };
        }
        return new(ended, sequence, segments, canTrim &&
            segments.All(segment => segment.Uri.Scheme is "http" or "https" && segment.Duration is > 0 and <= int.MaxValue),
            targetDuration);
    }

    /// <summary>读取 HLS 变体排序和节目映射所需的属性。</summary>
    /// <param name="playlist">已按主清单地址解析的变体清单。</param>
    /// <param name="attributes">变体的 HLS 属性列表。</param>
    /// <param name="program">FFmpeg 按变体在主清单中的顺序分配的节目编号。</param>
    /// <returns>包含分辨率、带宽和独立轨道信息的变体。</returns>
    private static HlsVariant ReadVariant(Uri playlist, string attributes, int program)
    {
        var values = ReadHlsAttributes(attributes);
        long pixels = 0;
        if (values.TryGetValue("RESOLUTION", out string? resolution))
        {
            int separator = resolution.IndexOf('x');
            if (separator > 0 && int.TryParse(resolution.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out int width) &&
                int.TryParse(resolution.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int height))
                pixels = (long)width * height;
        }
        long.TryParse(values.GetValueOrDefault("BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out long bandwidth);
        return new(playlist, pixels, bandwidth, values.GetValueOrDefault("AUDIO"), values.GetValueOrDefault("VIDEO"), program);
    }

    /// <summary>拆分 HLS 属性列表，保留引号内的逗号。</summary>
    /// <param name="attributes">HLS 标记冒号后的文本。</param>
    /// <returns>属性名称及其值。</returns>
    private static Dictionary<string, string> ReadHlsAttributes(string attributes)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match attribute in HlsAttribute.Matches(attributes))
            values[attribute.Groups[1].Value] = attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Value.Trim();
        return values;
    }

    private static async Task<HttpResponseMessage> GetHttpMediaAsync(Uri source, CancellationToken token, MediaRequestHeaders? headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        headers?.Apply(request);
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    /// <summary>保存普通 HTTP 文件，或使用 FFmpeg 拉取并封装流媒体。</summary>
    /// <param name="source">待下载的媒体地址。</param>
    /// <param name="directory">成品输出目录。</param>
    /// <param name="progress">可选的任务进度接收方。</param>
    /// <param name="token">用于停止任务的取消令牌。</param>
    /// <param name="headers">可选的媒体请求头。</param>
    /// <param name="selection">同一媒体源已经选定的网络入口。</param>
    /// <returns>成品文件的完整路径。</returns>
    /// <exception cref="MediaPublishException">媒体已经下载完成，但未能移到成品路径。</exception>
    public async Task<string> DownloadAsync(Uri source, string directory, IProgress<MediaProgress>? progress, CancellationToken token,
        MediaRequestHeaders? headers = null, NetworkMediaSelection? selection = null)
    {
        bool httpSource = source.Scheme is "http" or "https";
        if (httpSource && selection?.IsHls != true && !IsPlaylistPath(source.AbsolutePath))
        {
            string? directFile = await TryDownloadHttpAsync(source, directory, progress, token, headers).ConfigureAwait(false);
            if (directFile is not null)
                return directFile;
        }

        selection ??= await SelectNetworkMediaAsync(source, token, headers).ConfigureAwait(false);
        DateTime started = DateTime.Now;
        string output = Path.Combine(Path.GetFullPath(directory), TimestampedName("video", started) + ".mkv");
        return await WriteMediaAsync(output, NetworkInput(selection.Source, hls: httpSource, headers: headers), ["-c", "copy"], "matroska", progress, token,
            audioName: TimestampedName("audio", started), program: selection.Program).ConfigureAwait(false);
    }

    /// <summary>发布已下载完毕的 M4A 音频。</summary>
    /// <param name="stagedFile">完整的 M4A 缓存文件。</param>
    /// <param name="directory">成品输出目录。</param>
    /// <param name="completedAt">用于生成文件名的完成时间。</param>
    /// <returns>成品音频路径。同名时自动添加序号。</returns>
    /// <exception cref="MediaPublishException">发布失败，异常提供缓存文件位置。</exception>
    public static string SaveDownloadedAudio(string stagedFile, string directory, DateTime completedAt) =>
        MediaOutput.Publish(stagedFile, directory, TimestampedName("audio", completedAt), ".m4a");

    /// <summary>启动 FFmpeg，把网络媒体复制到供播放器读取的 MPEG-TS 管道。</summary>
    /// <param name="source">预览源地址。</param>
    /// <param name="token">用于停止供流进程的令牌。</param>
    /// <param name="headers">可选的媒体请求头。</param>
    /// <param name="hls">是否已识别为 HLS。</param>
    /// <param name="program">可选的 FFmpeg 节目编号。</param>
    /// <returns>预览流及其进程监控。调用方负责异步释放。</returns>
    /// <exception cref="InvalidOperationException">FFmpeg 缺失或无法启动。</exception>
    public PreviewStream OpenNetworkPreview(Uri source, CancellationToken token, MediaRequestHeaders? headers = null,
        bool hls = false, int? program = null)
    {
        var start = ToolProcess.CreateStartInfo(ToolProcess.RequireExecutable(ffmpeg, "FFmpeg"), cacheDirectory);
        string[] arguments = ["-hide_banner", "-loglevel", "warning", "-nostdin", .. NetworkInput(source, hls, headers),
            .. MapStreams(program), "-c", "copy", "-f", "mpegts", "pipe:1"];
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return PreviewStream.Start(start, "FFmpeg 无法读取预览媒体流。", token);
    }

    /// <summary>按编号读取一个录制目录内的分片并生成单个文件。</summary>
    /// <param name="recordingDirectory">包含有效录制分片的目录。</param>
    /// <param name="directory">成品输出目录。</param>
    /// <param name="encoder">需要重新编码视频时选用的编码器。</param>
    /// <param name="copy">是否直接复制选中的音视频轨道。启用时不应用重新编码设置。</param>
    /// <param name="progress">可选的任务进度接收方。</param>
    /// <param name="token">用于停止任务的取消令牌。</param>
    /// <param name="deinterlace">重新编码视频时所用的去隔行模式。</param>
    /// <param name="settings">可选的编码设置。省略时采用服务默认值。</param>
    /// <returns>合并成品的完整路径。</returns>
    /// <exception cref="InvalidDataException">分片序列无效。</exception>
    /// <exception cref="MediaPublishException">合并成品未能移到输出目录。</exception>
    public async Task<string> MergeAsync(string recordingDirectory, string directory, VideoEncoder encoder, bool copy,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null)
    {
        recordingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingDirectory));
        FileInfo[] parts = RecordingParts.Find(recordingDirectory);
        if (parts.Length == 0)
            throw new InvalidOperationException("所选文件夹没有可用的 Live Recorder 录制分片。");
        string name = Path.GetFileName(recordingDirectory);
        bool matroska = parts[0].Extension == RecordingParts.MatroskaExtension;
        string[] input = matroska
            ? ["-nostdin", "-protocol_whitelist", "file,pipe", "-f", "concat", "-safe", "0", "-i", "pipe:0"]
            : ["-nostdin", "-f", "mpegts", "-i", "pipe:0"];
        byte[]? concatList = matroska ? RecordingParts.MatroskaConcatList(parts) : null;
        SourceEncodingInfo source = await ProbeEncodingAsync(input, token, WritePartsAsync).ConfigureAwait(false);
        EncodingSettings encoding = settings ?? DefaultTranscodeSettings;
        string[] codecs = copy ? ["-c", "copy"] : EncodingArguments.Create(encoder, deinterlace, encoding, source);
        var format = OutputFormat(source, copy ? source.AudioCodec : EncodingArguments.OutputAudioFormat(encoding, source));
        string output = Path.Combine(Path.GetFullPath(directory), (name.Length == 0 ? "record" : name) + format.Extension);
        return await WriteEditedMediaAsync(output, input, codecs, format.Muxer, progress, token, WritePartsAsync).ConfigureAwait(false);

        // TS 保留连续时间戳。独立 Matroska 文件交由 concat demuxer 逐个读取。
        async Task WritePartsAsync(Stream pipe, CancellationToken ct)
        {
            if (concatList is not null)
            {
                await pipe.WriteAsync(concatList, ct).ConfigureAwait(false);
                return;
            }
            foreach (FileInfo part in parts)
            {
                await using var file = new FileStream(part.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await file.CopyToAsync(pipe, CopyBuffer, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>探测本地媒体并按轨道类型应用编码设置。</summary>
    /// <param name="source">输入媒体的本地文件路径。</param>
    /// <param name="directory">成品输出目录。</param>
    /// <param name="encoder">需要重新编码视频时选用的编码器。</param>
    /// <param name="progress">可选的任务进度接收方。</param>
    /// <param name="token">用于停止任务的取消令牌。</param>
    /// <param name="deinterlace">重新编码视频时所用的去隔行模式。</param>
    /// <param name="settings">可选的编码设置。省略时采用服务默认值。</param>
    /// <returns>转码成品的完整路径。</returns>
    /// <exception cref="FileNotFoundException">输入文件不存在。</exception>
    /// <exception cref="MediaPublishException">转码成品未能移到输出目录。</exception>
    public async Task<string> TranscodeAsync(string source, string directory, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("找不到需要转码的媒体文件。", source);
        string[] input = ["-protocol_whitelist", "file", "-i", Path.GetFullPath(source)];
        SourceEncodingInfo streams = await ProbeEncodingAsync(input, token).ConfigureAwait(false);
        EncodingSettings encoding = settings ?? DefaultTranscodeSettings;
        string[] codecs = EncodingArguments.Create(encoder, deinterlace, encoding, streams);
        var format = OutputFormat(streams, EncodingArguments.OutputAudioFormat(encoding, streams));
        return await WriteEditedMediaAsync(NewOutput(directory, "transcoded", format.Extension), input, codecs, format.Muxer, progress, token)
            .ConfigureAwait(false);
    }

    private static readonly EncodingSettings DefaultTranscodeSettings = new()
    {
        KeepVideoFormat = false,
        RateMode = VideoRateMode.Quality,
        KeepAudioFormat = false,
        KeepAudioBitrate = false
    };

    // 只探测轨道信息，不生成媒体，这样可为纯音频和纯视频分别选用编码参数。
    private async Task<SourceEncodingInfo> ProbeEncodingAsync(string[] input, CancellationToken token,
        Func<Stream, CancellationToken, Task>? writeInput = null)
    {
        var source = new SourceEncodingInfo();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await FfmpegProcess.RunAsync(ffmpeg, cacheDirectory,
            ["-loglevel", "info", .. input, .. MapStreams(), "-c", "copy", "-t", "0", "-f", "null", "-"],
            null, timeout.Token, writeInput: writeInput is null ? null : WriteProbeInputAsync, readDiagnostic: source.Read)
            .ConfigureAwait(false);
        if (!source.HasVideo && source.AudioCodec is null)
            throw new InvalidOperationException("找不到可用的音频或视频轨道。");
        return source;

        async Task WriteProbeInputAsync(Stream stream, CancellationToken ct)
        {
            try { await writeInput!(stream, ct).ConfigureAwait(false); }
            catch (IOException) when (!ct.IsCancellationRequested) { /* The probe closes its input after reading stream information. */ }
        }
    }

    private static (string Extension, string Muxer) OutputFormat(SourceEncodingInfo source, string? audioFormat) =>
        source.HasVideo ? (".mp4", "mp4") : audioFormat switch
        {
            "aac" => (".m4a", "mp4"),
            "mp3" => (".mp3", "mp3"),
            "ac3" => (".ac3", "ac3"),
            "eac3" => (".eac3", "eac3"),
            "opus" => (".opus", "opus"),
            "vorbis" => (".ogg", "ogg"),
            "flac" => (".flac", "flac"),
            _ => (".mka", "matroska")
        };

    private async Task<string> WriteEditedMediaAsync(string output, string[] input, string[] codecs, string format,
        IProgress<MediaProgress>? progress, CancellationToken token, Func<Stream, CancellationToken, Task>? writeInput = null)
    {
        try { return await WriteMediaAsync(output, input, codecs, format, progress, token, writeInput).ConfigureAwait(false); }
        catch (FfmpegFailureException ex) when (format == "mp4" && ex.Diagnostics.Any(line =>
            line.Contains("Could not find tag for codec", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("not currently supported in container", StringComparison.OrdinalIgnoreCase)))
        {
            return await WriteMediaAsync(Path.ChangeExtension(output, ".mkv"), input, codecs, "matroska", progress, token, writeInput)
                .ConfigureAwait(false);
        }
    }

    // FFmpeg 先写便携缓存。任务仍有效且文件完整写出后才移到成品路径。
    private async Task<string> WriteMediaAsync(string output, string[] input, string[] codecs, string format,
        IProgress<MediaProgress>? progress, CancellationToken token,
        Func<Stream, CancellationToken, Task>? writeInput = null, string? audioName = null, int? program = null)
    {
        string directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(cacheDirectory);
        string partial = Path.Combine(cacheDirectory, $"media-{Guid.NewGuid():N}.part");
        string[] containerOptions = format == "mp4" ? ["-movflags", "+faststart"] : [];
        bool hasVideo = false;
        bool preservePartial = false;
        Action? noteVideo = audioName is null ? null : () => hasVideo = true;
        try
        {
            await FfmpegProcess.RunAsync(ffmpeg, cacheDirectory,
                ["-n", .. input, .. MapStreams(program), .. codecs, .. containerOptions, "-f", format, partial],
                progress, token, writeInput: writeInput, videoWritten: noteVideo).ConfigureAwait(false);
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
                throw new InvalidOperationException("FFmpeg 没有生成输出文件。");
            token.ThrowIfCancellationRequested();
            string name = !hasVideo && audioName is not null ? audioName : Path.GetFileNameWithoutExtension(output);
            return MediaOutput.Publish(partial, directory, name, Path.GetExtension(output));
        }
        catch (MediaPublishException) { preservePartial = true; throw; }
        finally { if (!preservePartial) File.Delete(partial); }
    }

    private async Task<string?> TryDownloadHttpAsync(Uri source, string directory,
        IProgress<MediaProgress>? progress, CancellationToken token, MediaRequestHeaders? headers)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await GetHttpMediaAsync(source, timeout.Token, headers).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        Uri finalUri = response.RequestMessage?.RequestUri ?? source;
        var disposition = response.Content.Headers.ContentDisposition;
        string name = disposition?.FileNameStar ?? disposition?.FileName ?? Uri.UnescapeDataString(finalUri.Segments.LastOrDefault() ?? "");
        name = name.Trim('"');
        string extension = Path.GetExtension(name).ToLowerInvariant();
        if (IsPlaylistPath(name) || IsPlaylistPath(finalUri.AbsolutePath) ||
            mime is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl" or "audio/x-mpegurl")
            return null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        byte[] buffer = new byte[CopyBuffer];
        int headerLength = await stream.ReadAtLeastAsync(buffer.AsMemory(0, 256), 256, throwOnEndOfStream: false,
            timeout.Token).ConfigureAwait(false);
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        string header = Encoding.UTF8.GetString(buffer, 0, headerLength).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (header.StartsWith("#EXTM3U", StringComparison.Ordinal))
            return null;
        if (mime is "text/html" or "application/json" || header.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("该地址返回网页或接口数据。请粘贴实际的媒体链接。");
        if (headerLength == 0)
            throw new InvalidDataException("服务器返回了空文件。");

        string safeExtension = extension.Length is >= 2 and <= 10 && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? extension : mime switch { "video/mp4" => ".mp4", "video/webm" => ".webm", "video/mp2t" => ".ts",
                "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/aac" => ".aac", _ => ".bin" };
        bool audio = mime.StartsWith("audio/", StringComparison.Ordinal) ||
            (!mime.StartsWith("video/", StringComparison.Ordinal) && IsAudioExtension(safeExtension));
        string output = NewOutput(directory, audio ? "audio" : "video", safeExtension);
        Directory.CreateDirectory(cacheDirectory);
        string partial = Path.Combine(cacheDirectory, $"download-{Guid.NewGuid():N}.part");
        long? expected = response.Content.Headers.ContentLength;
        bool preservePartial = false;
        try
        {
            long received = headerLength;
            var reportTimer = Stopwatch.StartNew();
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                CopyBuffer, FileOptions.Asynchronous))
            {
                await file.WriteAsync(buffer.AsMemory(0, headerLength), token).ConfigureAwait(false);
                while (true)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (reportTimer.ElapsedMilliseconds >= 200)
                    {
                        progress?.Report(new MediaProgress(Bytes: received, TotalBytes: expected));
                        reportTimer.Restart();
                    }
                }
            }
            if (expected is not null && received != expected)
                throw new IOException("下载字节数与服务器声明的大小不一致。");
            progress?.Report(new MediaProgress(Bytes: received, TotalBytes: expected));
            token.ThrowIfCancellationRequested();
            return MediaOutput.Publish(partial, Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output), Path.GetExtension(output));
        }
        catch (MediaPublishException) { preservePartial = true; throw; }
        finally { if (!preservePartial) File.Delete(partial); }
    }

    private static string[] NetworkInput(Uri source, bool hls = false, MediaRequestHeaders? headers = null)
    {
        if (source.Scheme is "rtsp" or "rtsps")
            return ["-protocol_whitelist", NetworkProtocols, "-rtsp_flags", "prefer_tcp",
                "-timeout", $"{DefaultTimeoutMicroseconds}", "-i", source.AbsoluteUri];

        bool http = source.Scheme is "http" or "https";
        string[] format = http && (hls || IsPlaylistPath(source.AbsolutePath)) ? ["-f", "hls"] : [];
        string[] request = http ? headers?.FfmpegArguments() ?? [] : [];
        string[] timeout = source.Scheme == "rtp" ? [] : ["-rw_timeout", $"{DefaultTimeoutMicroseconds}"];
        return ["-protocol_whitelist", NetworkProtocols, .. timeout, .. format, .. request, "-i", source.AbsoluteUri];
    }

    private static bool IsPlaylistPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".m3u8" or ".m3u";

    private static bool IsAudioExtension(string extension) =>
        extension is ".aac" or ".aif" or ".aiff" or ".flac" or ".m4a" or ".m4b" or ".mka" or ".mp3" or ".oga" or ".opus" or ".wav" or ".weba" or ".wma";

    /// <summary>映射选定节目或整个输入中的第一条视频及音频轨道。</summary>
    /// <param name="program">HLS 节目编号。省略时在整个输入中查找。</param>
    /// <returns>FFmpeg 轨道映射参数。</returns>
    private static string[] MapStreams(int? program = null)
    {
        string source = program is int id ? $"0:p:{id.ToString(CultureInfo.InvariantCulture)}" : "0";
        return ["-map", $"{source}:V:0?", "-map", $"{source}:a:0?", "-sn", "-dn"];
    }

    private static string NewOutput(string directory, string name, string extension) =>
        Path.Combine(Path.GetFullPath(directory), TimestampedName(name, DateTime.Now) + extension);

    private static string TimestampedName(string prefix, DateTime started) =>
        FormattableString.Invariant($"{prefix}_{started:yyyyMMdd_HHmmss}");

    // 用原生创建操作抢占目录名，避免多个实例同时录制时共用一个分片目录。
    private static string CreateRecordingDirectory(string directory, DateTime startedAt)
    {
        directory = Directory.CreateDirectory(directory).FullName;
        string name = "record_" + startedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        for (int count = 1; ; count++)
        {
            string destination = Path.Combine(directory, MediaOutput.AvailableName(name, count));
            if (CreateDirectoryW(destination, nint.Zero))
                return destination;
            int error = Marshal.GetLastWin32Error();
            if (error is not (80 or 183))
                throw new IOException($"无法创建录制文件夹 {destination}。", new Win32Exception(error));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, nint securityAttributes);

}
