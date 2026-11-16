using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LiveRecorder.Core;

public enum VideoEncoder { Software, Intel, Nvidia, Amd }
public enum DeinterlaceMode { Automatic, Always, Off }

public sealed record MediaProgress(long? Bytes = null, long? TotalBytes = null, int? Parts = null, TimeSpan? MediaTime = null);
public sealed record RecordingResult(string Directory, int PartCount, string? Interruption);

public sealed class MediaService(string ffmpeg)
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

    public async Task<string> DownloadAsync(Uri source, string directory, IProgress<MediaProgress>? progress,
        CancellationToken token, string? outputTitle = null)
    {
        Directory.CreateDirectory(directory);
        bool httpSource = source.Scheme is "http" or "https";
        if (httpSource && !IsPlaylistPath(source.AbsolutePath))
        {
            string? directFile = await TryDownloadHttpAsync(source, directory, progress, token, outputTitle).ConfigureAwait(false);
            if (directFile is not null)
                return directFile;
        }

        string output = outputTitle is null ? NewOutput(directory, "download", ".mkv")
            : Path.Combine(Path.GetFullPath(directory), EpisodeFileName(outputTitle) + ".mkv");
        return await WriteMediaAsync(output, NetworkInput(source, hls: httpSource), ["-c", "copy"], "matroska", progress, token)
            .ConfigureAwait(false);
    }

    public Task<RecordingResult> RecordAsync(Uri source, string directory, int partMegabytes, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic) =>
        RecordAsync(NetworkInput(source), directory, partMegabytes, encoder, progress, token, deinterlace);

    public async Task<VideoDimensions> ProbeVideoAsync(Uri source, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        VideoDimensions? dimensions = null;
        await FfmpegProcess.RunAsync(ffmpeg,
            ["-xerror", .. NetworkInput(source, hls: true), "-map", "0:V:0", "-an", "-sn", "-dn",
                "-frames:v", "1", "-c:v", "rawvideo", "-threads:v", "1", "-f", "framehash", "pipe:1"],
            null, timeout.Token, readOutput: async stream =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                const string prefix = "#dimensions 0: ";
                bool frameWritten = false;
                while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        string[] parts = line[prefix.Length..].Split('x');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height))
                            dimensions = new VideoDimensions(width, height);
                    }
                    else if (line.StartsWith("0,", StringComparison.Ordinal))
                        frameWritten = true;
                }
                if (!frameWritten)
                    dimensions = null;
            }).ConfigureAwait(false);
        return dimensions ?? throw new InvalidDataException("视频源未能解码出完整画面。");
    }

    public Task CreateLivePreviewAsync(Uri source, string directory, CancellationToken token) =>
        FfmpegProcess.RunAsync(ffmpeg,
            [.. NetworkInput(source), .. MapStreams(),
                "-vf", VideoFilters("scale=-2:trunc(min(ih\\,720)/2)*2", DeinterlaceMode.Automatic), "-pix_fmt", "yuv420p",
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                "-r", "30", "-g", "60", "-sc_threshold", "0",
                "-c:a", "aac", "-b:a", "128k",
                "-f", "hls", "-hls_time", "2", "-hls_list_size", "6",
                "-hls_flags", "delete_segments+independent_segments+temp_file",
                "-hls_segment_filename", Path.Combine(directory, "segment-%08d.ts"),
                Path.Combine(directory, "stream.m3u8")],
            null, token);

    public Task<RecordingResult> RecordSdpAsync(string path, string directory, int partMegabytes, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic)
    {
        if (!Path.GetExtension(path).Equals(".sdp", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("请选择有效的本地 SDP 文件。");
        return RecordAsync(["-protocol_whitelist", "file,udp,rtp", "-f", "sdp", "-i", Path.GetFullPath(path)],
            directory, partMegabytes, encoder, progress, token, deinterlace);
    }

    public Task<string> MergeAsync(string recordingDirectory, string directory, VideoEncoder encoder, bool copy,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic)
    {
        recordingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingDirectory));
        string[] parts = RecordingParts.Find(recordingDirectory);
        if (parts.Length == 0)
            throw new InvalidOperationException("所选文件夹没有可用的 Live Recorder 录制分片。");
        string name = Path.GetFileName(recordingDirectory);
        string output = Path.Combine(Path.GetFullPath(directory), (name.Length == 0 ? "record" : name) + ".mp4");
        return WriteMediaAsync(output, ["-nostdin", "-f", "mpegts", "-i", "pipe:0"],
            copy ? ["-c", "copy", "-bsf:a", "aac_adtstoasc"] : Encode(encoder, deinterlace), "mp4", progress, token,
            async (pipe, ct) =>
            {
                foreach (string part in parts)
                {
                    await using var file = File.OpenRead(part);
                    await file.CopyToAsync(pipe, CopyBuffer, ct).ConfigureAwait(false);
                }
            });
    }

    public Task<string> TranscodeAsync(string source, string directory, VideoEncoder encoder,
        IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace = DeinterlaceMode.Automatic)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("找不到需要转码的视频文件。", source);
        return WriteMediaAsync(NewOutput(directory, "transcoded", ".mp4"),
            ["-protocol_whitelist", "file", "-i", Path.GetFullPath(source)],
            Encode(encoder, deinterlace), "mp4", progress, token);
    }

    private async Task<RecordingResult> RecordAsync(string[] input, string directory, int partMegabytes,
        VideoEncoder encoder, IProgress<MediaProgress>? progress, CancellationToken token, DeinterlaceMode deinterlace)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partMegabytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partMegabytes, 1024);
        token.ThrowIfCancellationRequested();
        string recordingDirectory = CreateRecordingDirectory(directory, DateTime.Now);
        string? interruption = null;
        try
        {
            await FfmpegProcess.RunAsync(ffmpeg,
                [.. input, .. MapStreams(), .. Encode(encoder, deinterlace), "-f", "mpegts", "pipe:1"],
                progress, token,
                readOutput: stream => RecordingParts.SplitAsync(stream, recordingDirectory, partMegabytes * 1_000_000L, progress),
                finishOnCancellation: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            interruption = ex.Message;
        }

        int count = RecordingParts.Find(recordingDirectory).Length;
        if (count == 0)
        {
            if (!Directory.EnumerateFileSystemEntries(recordingDirectory).Any())
                Directory.Delete(recordingDirectory);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException(interruption ?? "录制结束，没有收到可用媒体数据。");
        }
        return new RecordingResult(recordingDirectory, count, interruption);
    }

    private async Task<string> WriteMediaAsync(string output, string[] input, string[] codecs, string format,
        IProgress<MediaProgress>? progress, CancellationToken token,
        Func<Stream, CancellationToken, Task>? writeInput = null)
    {
        string directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, $".media-{Guid.NewGuid():N}.part");
        string[] containerOptions = format == "mp4" ? ["-movflags", "+faststart"] : [];
        try
        {
            await FfmpegProcess.RunAsync(ffmpeg,
                ["-n", .. input, .. MapStreams(), .. codecs, .. containerOptions, "-f", format, partial],
                progress, token, writeInput: writeInput).ConfigureAwait(false);
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
                throw new InvalidOperationException("FFmpeg 没有生成输出文件。");
            token.ThrowIfCancellationRequested();
            return MoveToAvailableName(partial, directory, Path.GetFileNameWithoutExtension(output),
                Path.GetExtension(output), File.Move);
        }
        finally { File.Delete(partial); }
    }

    private static async Task<string?> TryDownloadHttpAsync(Uri source, string directory,
        IProgress<MediaProgress>? progress, CancellationToken token, string? outputTitle)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
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

        string stem = Path.GetFileNameWithoutExtension(name.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        stem = string.Concat(stem.Take(70).Select(character => invalid.Contains(character) ? '_' : character)).Trim(' ', '.');
        string safeExtension = extension.Length is >= 2 and <= 10 && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? extension : mime switch { "video/mp4" => ".mp4", "video/webm" => ".webm", "video/mp2t" => ".ts",
                "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/aac" => ".aac", _ => ".bin" };
        string output = NewOutput(directory, stem.Length == 0 ? "download" : "download-" + stem, safeExtension);
        string partial = output + ".part";
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
            if (outputTitle is not null)
                return MoveToAvailableName(partial, Path.GetFullPath(directory), EpisodeFileName(outputTitle), safeExtension, File.Move);
            File.Move(partial, output);
            return output;
        }
        finally { File.Delete(partial); }
    }

    private static string[] NetworkInput(Uri source, bool hls = false)
    {
        if (source.Scheme is "rtsp" or "rtsps")
            return ["-protocol_whitelist", NetworkProtocols, "-rtsp_flags", "prefer_tcp",
                "-timeout", $"{DefaultTimeoutMicroseconds}", "-i", source.AbsoluteUri];

        string[] format = (source.Scheme is "http" or "https") && (hls || IsPlaylistPath(source.AbsolutePath))
            ? ["-f", "hls"] : [];
        string[] timeout = source.Scheme == "rtp" ? [] : ["-rw_timeout", $"{DefaultTimeoutMicroseconds}"];
        return ["-protocol_whitelist", NetworkProtocols, .. timeout, .. format, "-i", source.AbsoluteUri];
    }

    private static bool IsPlaylistPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".m3u8" or ".m3u";

    private static string EpisodeFileName(string title)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string name = string.Concat(title.Take(100).Select(character => invalid.Contains(character) ? '_' : character))
            .Trim(' ', '.');
        return name.Length == 0 ? "CCTV 节目" : "CCTV - " + name;
    }

    private static string[] MapStreams() => ["-map", "0:V:0?", "-map", "0:a:0?", "-sn", "-dn"];

    private static string[] Encode(VideoEncoder encoder, DeinterlaceMode deinterlace)
    {
        string codec = encoder switch
        {
            VideoEncoder.Software => "libx264",
            VideoEncoder.Intel => "h264_qsv",
            VideoEncoder.Nvidia => "h264_nvenc",
            VideoEncoder.Amd => "h264_amf",
            _ => throw new ArgumentOutOfRangeException(nameof(encoder))
        };
        string pixelFormat = encoder == VideoEncoder.Intel ? "nv12" : "yuv420p";
        return ["-vf", VideoFilters("scale=trunc(iw/2)*2:trunc(ih/2)*2", deinterlace), "-pix_fmt", pixelFormat,
            "-c:v", codec, "-c:a", "aac", "-b:a", "160k"];
    }

    private static string VideoFilters(string scale, DeinterlaceMode deinterlace) => deinterlace switch
    {
        DeinterlaceMode.Automatic => $"bwdif=mode=send_frame:parity=auto:deint=interlaced,{scale}",
        DeinterlaceMode.Always => $"bwdif=mode=send_frame:parity=auto:deint=all,{scale}",
        DeinterlaceMode.Off => scale,
        _ => throw new ArgumentOutOfRangeException(nameof(deinterlace))
    };

    private static string NewOutput(string directory, string name, string extension) =>
        Path.Combine(Path.GetFullPath(directory), $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension}");

    private static string CreateRecordingDirectory(string directory, DateTime startedAt)
    {
        directory = Directory.CreateDirectory(directory).FullName;
        string name = "record_" + startedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string temporary = Path.Combine(directory, $".record-{Guid.NewGuid():N}.part");
        Directory.CreateDirectory(temporary);
        try
        {
            // Directory.CreateDirectory would also succeed for an existing recording.
            return MoveToAvailableName(temporary, directory, name, "", Directory.Move);
        }
        finally
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary);
        }
    }

    private static string MoveToAvailableName(string source, string directory, string name, string extension,
        Action<string, string> move)
    {
        const int fileExists = unchecked((int)0x80070050);
        const int alreadyExists = unchecked((int)0x800700B7);

        for (int count = 1; ; count++)
        {
            string stem = count == 1 ? name : FormattableString.Invariant($"{name} ({count})");
            string destination = Path.Combine(directory, stem + extension);
            if (Path.Exists(destination))
                continue;
            try
            {
                move(source, destination);
                return destination;
            }
            catch (IOException ex) when (ex.HResult is fileExists or alreadyExists || Path.Exists(destination))
            {
                // Another task may have claimed the name after the existence check.
            }
        }
    }
}
