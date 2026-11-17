using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace LiveRecorder.Core;

public enum VideoEncoder { Software, Intel, Nvidia, Amd }
public enum DeinterlaceMode { Automatic, Always, Off }

public sealed record MediaProgress(long? Bytes = null, long? TotalBytes = null, int? Parts = null, TimeSpan? MediaTime = null);
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

    public static Uri ParseSource(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Scheme is not ("http" or "https" or "rtp" or "udp" or "rtmp" or "rtmps" or "rtmpt" or "rtmpts" or "rtsp" or "rtsps" or "srt"))
            throw new ArgumentException("请输入 HTTP(S)、RTSP(S)、RTMP(S)、RTP、UDP 或 SRT 媒体链接。");
        return uri;
    }

    public static async Task<bool> HasFixedPreviewDurationAsync(Uri source, CancellationToken token, MediaRequestHeaders? headers = null) =>
        (await InspectHttpMediaAsync(source, token, headers).ConfigureAwait(false))?.HasFixedDuration == true;

    private sealed record HlsSegment(Uri Uri, decimal? Duration);
    private sealed record HttpMediaState(bool HasFixedDuration, long HlsSequence = 0, IReadOnlyList<HlsSegment>? HlsSegments = null,
        bool CanTrimHls = false);

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
        Uri? variant = null;
        bool variantPath = false;
        bool ended = false;
        bool canTrim = true;
        long sequence = 0;
        decimal? duration = null;
        var segments = new List<HlsSegment>();
        while (lines.ReadLine()?.Trim() is { } line)
        {
            if (line == "#EXT-X-ENDLIST")
                ended = true;
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                long.TryParse(line.AsSpan("#EXT-X-MEDIA-SEQUENCE:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> value = line.AsSpan("#EXTINF:".Length);
                int comma = value.IndexOf(',');
                duration = decimal.TryParse(comma < 0 ? value : value[..comma], NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal seconds) ? seconds : null;
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                variantPath = true;
            else if (variantPath && line.Length != 0 && !line.StartsWith('#'))
            {
                if (variant is null)
                    Uri.TryCreate(location, line, out variant);
                variantPath = false;
            }
            else if (line.Length != 0 && !line.StartsWith('#'))
            {
                if (Uri.TryCreate(location, line, out Uri? segment))
                    segments.Add(new(segment, duration));
                duration = null;
            }
            else if (line.StartsWith("#EXT-X-", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) &&
                !line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal) &&
                line != "#EXT-X-INDEPENDENT-SEGMENTS")
                canTrim = false;
        }
        if (variant is not null)
        {
            HttpMediaState? child = await ReadHttpMediaAsync(variant, depth + 1, token, headers).ConfigureAwait(false);
            return child is null ? null : child with { CanTrimHls = false };
        }
        return new(ended, sequence, segments, canTrim &&
            segments.All(segment => segment.Uri.Scheme is "http" or "https" && segment.Duration is > 0 and <= int.MaxValue));
    }

    private static async Task<HttpResponseMessage> GetHttpMediaAsync(Uri source, CancellationToken token, MediaRequestHeaders? headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        headers?.Apply(request);
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    public async Task<string> DownloadAsync(Uri source, string directory, IProgress<MediaProgress>? progress, CancellationToken token,
        MediaRequestHeaders? headers = null)
    {
        bool httpSource = source.Scheme is "http" or "https";
        if (httpSource && !IsPlaylistPath(source.AbsolutePath))
        {
            string? directFile = await TryDownloadHttpAsync(source, directory, progress, token, headers).ConfigureAwait(false);
            if (directFile is not null)
                return directFile;
        }

        DateTime started = DateTime.Now;
        string output = Path.Combine(Path.GetFullPath(directory), TimestampedName("video", started) + ".mkv");
        return await WriteMediaAsync(output, NetworkInput(source, hls: httpSource, headers: headers), ["-c", "copy"], "matroska", progress, token,
            audioName: TimestampedName("audio", started)).ConfigureAwait(false);
    }

    public static string SaveDownloadedAudio(string stagedFile, string directory, DateTime completedAt) =>
        MoveToAvailableName(stagedFile, Directory.CreateDirectory(directory).FullName, FormattableString.Invariant($"audio_{completedAt:yyyyMMdd_HHmmss}"),
            ".m4a");

    public PreviewStream OpenNetworkPreview(Uri source, CancellationToken token, MediaRequestHeaders? headers = null)
    {
        var start = ToolProcess.CreateStartInfo(ToolProcess.RequireExecutable(ffmpeg, "FFmpeg"), cacheDirectory);
        string[] arguments = ["-hide_banner", "-loglevel", "warning", "-nostdin", .. NetworkInput(source, headers: headers),
            .. MapStreams(), "-c", "copy", "-f", "mpegts", "pipe:1"];
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return PreviewStream.Start(start, "FFmpeg 无法读取预览媒体流。", token);
    }

    public async Task<string> MergeAsync(string recordingDirectory, string directory, VideoEncoder encoder, bool copy,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic,
        EncodingSettings? settings = null)
    {
        recordingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingDirectory));
        FileInfo[] parts = RecordingParts.Find(recordingDirectory);
        if (parts.Length == 0)
            throw new InvalidOperationException("所选文件夹没有可用的 Live Recorder 录制分片。");
        string name = Path.GetFileName(recordingDirectory);
        string[] input = ["-nostdin", "-f", "mpegts", "-i", "pipe:0"];
        SourceEncodingInfo source = await ProbeEncodingAsync(input, token, WritePartsAsync).ConfigureAwait(false);
        EncodingSettings encoding = settings ?? DefaultTranscodeSettings;
        string[] codecs = copy ? ["-c", "copy"] : EncodingArguments.Create(encoder, deinterlace, encoding, source);
        var format = OutputFormat(source, copy ? source.AudioCodec : EncodingArguments.OutputAudioFormat(encoding, source));
        string output = Path.Combine(Path.GetFullPath(directory), (name.Length == 0 ? "record" : name) + format.Extension);
        return await WriteEditedMediaAsync(output, input, codecs, format.Muxer, progress, token, WritePartsAsync).ConfigureAwait(false);

        async Task WritePartsAsync(Stream pipe, CancellationToken ct)
        {
            foreach (FileInfo part in parts)
            {
                await using var file = new FileStream(part.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await file.CopyToAsync(pipe, CopyBuffer, ct).ConfigureAwait(false);
            }
        }
    }

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

    private async Task<string> WriteMediaAsync(string output, string[] input, string[] codecs, string format,
        IProgress<MediaProgress>? progress, CancellationToken token,
        Func<Stream, CancellationToken, Task>? writeInput = null, string? audioName = null)
    {
        string directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(cacheDirectory);
        string partial = Path.Combine(cacheDirectory, $"media-{Guid.NewGuid():N}.part");
        string[] containerOptions = format == "mp4" ? ["-movflags", "+faststart"] : [];
        bool hasVideo = false;
        Action? noteVideo = audioName is null ? null : () => hasVideo = true;
        try
        {
            await FfmpegProcess.RunAsync(ffmpeg, cacheDirectory,
                ["-n", .. input, .. MapStreams(), .. codecs, .. containerOptions, "-f", format, partial],
                progress, token, writeInput: writeInput, videoWritten: noteVideo).ConfigureAwait(false);
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
                throw new InvalidOperationException("FFmpeg 没有生成输出文件。");
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            string name = !hasVideo && audioName is not null ? audioName : Path.GetFileNameWithoutExtension(output);
            return MoveToAvailableName(partial, directory, name, Path.GetExtension(output));
        }
        finally { File.Delete(partial); }
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
            string destination = Directory.CreateDirectory(Path.GetDirectoryName(output)!).FullName;
            return MoveToAvailableName(partial, destination, Path.GetFileNameWithoutExtension(output), Path.GetExtension(output));
        }
        finally { File.Delete(partial); }
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

    private static string[] MapStreams() => ["-map", "0:V:0?", "-map", "0:a:0?", "-sn", "-dn"];

    private static string NewOutput(string directory, string name, string extension) =>
        Path.Combine(Path.GetFullPath(directory), TimestampedName(name, DateTime.Now) + extension);

    private static string TimestampedName(string prefix, DateTime started) =>
        FormattableString.Invariant($"{prefix}_{started:yyyyMMdd_HHmmss}");

    private static string CreateRecordingDirectory(string directory, DateTime startedAt)
    {
        directory = Directory.CreateDirectory(directory).FullName;
        string name = "record_" + startedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        for (int count = 1; ; count++)
        {
            string destination = Path.Combine(directory, AvailableName(name, count));
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

    private static string AvailableName(string name, int count) =>
        count == 1 ? name : FormattableString.Invariant($"{name} ({count})");

    private static string MoveToAvailableName(string source, string directory, string name, string extension)
    {
        const int fileExists = unchecked((int)0x80070050);
        const int alreadyExists = unchecked((int)0x800700B7);

        for (int count = 1; ; count++)
        {
            string destination = Path.Combine(directory, AvailableName(name, count) + extension);
            if (Path.Exists(destination))
                continue;
            try
            {
                File.Move(source, destination);
                return destination;
            }
            catch (IOException ex) when (ex.HResult is fileExists or alreadyExists || Path.Exists(destination))
            {
                // Another task may have claimed the name after the existence check.
            }
        }
    }
}
