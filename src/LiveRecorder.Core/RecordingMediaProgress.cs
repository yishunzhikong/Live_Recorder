using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

/// <summary>当前 FFmpeg 录制尝试的封装格式、输出轨道和分轨累计数据字节。</summary>
/// <param name="Attempt">单调时钟上的尝试标识，用于在重连或换封装时重置统计。</param>
/// <param name="Container">分片封装格式。</param>
/// <param name="Video">实际输出视频轨道的说明。</param>
/// <param name="Audio">实际输出音频轨道的说明。</param>
/// <param name="VideoBytes">提交给封装器的视频包累计字节数。</param>
/// <param name="AudioBytes">提交给封装器的音频包累计字节数。</param>
/// <param name="VideoReencoded">当前录制尝试是否对视频轨道重新编码。</param>
/// <param name="AudioReencoded">当前录制尝试是否对音频轨道重新编码。</param>
public sealed record RecordingMediaProgress(long Attempt, string Container, string? Video = null, string? Audio = null,
    long? VideoBytes = null, long? AudioBytes = null, bool VideoReencoded = false, bool AudioReencoded = false);

/// <summary>解析录制轨道及包大小，随 FFmpeg 的媒体时间一起发布累计字节。</summary>
internal sealed partial class RecordingStatistics(string extension, IProgress<MediaProgress> progress, bool videoReencoded, bool audioReencoded)
    : IProgress<MediaProgress>
{
    private RecordingMediaProgress _current = new(Stopwatch.GetTimestamp(),
        extension == RecordingParts.MatroskaExtension ? "MKV" : "TS",
        VideoReencoded: videoReencoded, AudioReencoded: audioReencoded);
    private bool _output;
    private long? _videoBytes;
    private long? _audioBytes;

    /// <summary>发布新录制尝试的初始状态。</summary>
    public void Start() => progress.Report(new MediaProgress(Bytes: 0, Parts: 0, MediaTime: TimeSpan.Zero, Recording: _current));

    /// <summary>读取一行 FFmpeg 的输出描述或封装包诊断。</summary>
    /// <param name="line">FFmpeg 标准错误流的一行。</param>
    public void Read(string line)
    {
        if (line.StartsWith("Output #0,", StringComparison.Ordinal))
            _output = true;

        if (_output && line.AsSpan().TrimStart().StartsWith("Stream #0:", StringComparison.Ordinal))
        {
            Match track = OutputTrack().Match(line);
            if (!track.Success)
                return;
            bool video = track.Groups[1].Value == "Video";
            string codec = track.Groups[2].Value.ToUpperInvariant();
            string? format = MatchValue(video ? VideoSize() : AudioLayout(), line);
            string? fps = video ? MatchValue(FrameRate(), line) : null;
            string description = string.Join(" · ", new[] { codec, format, fps }.OfType<string>());
            _current = video ? _current with { Video = description } : _current with { Audio = description };
            return;
        }

        const string packetMarker = "muxer <- ";
        int marker = line.IndexOf(packetMarker, StringComparison.Ordinal);
        if (marker < 0)
            return;
        int start = line.IndexOf(" size:", marker, StringComparison.Ordinal);
        if (start < 0)
            return;
        ReadOnlySpan<char> size = line.AsSpan(start + " size:".Length);
        int end = size.IndexOf(' ');
        if (!int.TryParse(end < 0 ? size : size[..end], NumberStyles.None, CultureInfo.InvariantCulture, out int bytes))
            return;

        ReadOnlySpan<char> packet = line.AsSpan(marker + packetMarker.Length);
        if (line.StartsWith("[vost#", StringComparison.Ordinal) || packet.StartsWith("type:video", StringComparison.Ordinal))
            _videoBytes = (_videoBytes ?? 0) + bytes;
        else if (line.StartsWith("[aost#", StringComparison.Ordinal) || packet.StartsWith("type:audio", StringComparison.Ordinal))
            _audioBytes = (_audioBytes ?? 0) + bytes;
    }

    // 媒体时间和包诊断来自同一条 stderr 管道。每次进度都带上已读取的全部包字节，
    // 即使一个 HLS 分片的包在两个进度报告之间集中到达，也会计入下次读数。
    void IProgress<MediaProgress>.Report(MediaProgress value) =>
        progress.Report(value with { Recording = _current with { VideoBytes = _videoBytes, AudioBytes = _audioBytes } });

    private static string? MatchValue(Regex field, string line)
    {
        Match match = field.Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^\s*Stream #0:\d+.*?: (Video|Audio):\s+([\w-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OutputTrack();

    [GeneratedRegex(@"(?:, )([1-9][0-9]*x[1-9][0-9]*)(?=[ ,\[]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VideoSize();

    [GeneratedRegex(@"(?:, )([0-9]+(?:\.[0-9]+)? fps)(?:,|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FrameRate();

    [GeneratedRegex(@"(?:, )([0-9]+ Hz, [^,]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AudioLayout();
}
