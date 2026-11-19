using System.Globalization;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

/// <summary>指定重新编码的视频格式。</summary>
public enum VideoCodec
{
    /// <summary>H.264（AVC）。</summary>
    H264,

    /// <summary>H.265（HEVC）。</summary>
    H265
}
/// <summary>指定输出高度，输出宽度按源画幅计算。</summary>
public enum VideoResolution
{
    /// <summary>目标高度为 360 像素。</summary>
    P360,

    /// <summary>目标高度为 480 像素。</summary>
    P480,

    /// <summary>目标高度为 720 像素。</summary>
    P720,

    /// <summary>目标高度为 1080 像素。</summary>
    P1080,

    /// <summary>目标高度为 1440 像素。</summary>
    P1440,

    /// <summary>目标高度为 2160 像素。</summary>
    P2160
}
/// <summary>选择用码率或编码器质量目标控制视频输出。</summary>
public enum VideoRateMode
{
    /// <summary>使用原始码率或指定目标码率。</summary>
    Bitrate,

    /// <summary>使用编码器支持的质量档位。</summary>
    Quality
}
/// <summary>指定按目标码率编码时的码率模式。</summary>
public enum BitrateMode
{
    /// <summary>允许场景之间的码率波动。</summary>
    Variable,

    /// <summary>向编码器请求恒定码率。</summary>
    Constant
}
/// <summary>定义码率字段的十进制单位。</summary>
public enum BitrateUnit
{
    /// <summary>千比特每秒。</summary>
    Kbps,

    /// <summary>兆比特每秒，等于 1000 kbps。</summary>
    Mbps
}
/// <summary>定义质量模式的三个档位。</summary>
public enum EncodingQuality
{
    /// <summary>优先减小成品体积。</summary>
    Smaller,

    /// <summary>使用默认的质量档位。</summary>
    Standard,

    /// <summary>优先保留更多画面细节。</summary>
    Higher
}
/// <summary>指定重新编码的音频格式。</summary>
public enum AudioFormat
{
    /// <summary>高级音频编码（AAC）。</summary>
    Aac,

    /// <summary>MPEG Layer III（MP3）。</summary>
    Mp3,

    /// <summary>Dolby Digital（AC-3）。</summary>
    Ac3,

    /// <summary>Opus 音频编码。</summary>
    Opus
}

/// <summary>为视频与音频轨道保存复制或重新编码的设置。</summary>
/// <remarks>两类轨道分别判断能否直接复制。保持原格式仍允许因分辨率、码率或其他选项而重新编码。</remarks>
public sealed record EncodingSettings
{
    /// <summary>重新编码且不保留源格式时使用的视频编码。</summary>
    public VideoCodec VideoCodec { get; init; }
    /// <summary>重新编码时是否沿用源视频编码格式。</summary>
    public bool KeepVideoFormat { get; init; } = true;
    /// <summary>是否保留源视频宽高。</summary>
    public bool KeepResolution { get; init; } = true;
    /// <summary>不保留原分辨率时使用的目标高度档位。</summary>
    public VideoResolution Resolution { get; init; } = VideoResolution.P1080;
    /// <summary>控制视频编码码率或质量的方式。</summary>
    public VideoRateMode RateMode { get; init; }
    /// <summary>指定目标码率时使用的可变或恒定码率模式。</summary>
    public BitrateMode BitrateMode { get; init; }
    /// <summary>按码率控制时是否采用源视频声明的码率。</summary>
    public bool KeepVideoBitrate { get; init; } = true;
    /// <summary>不保留源码率时使用的数值，单位由 VideoBitrateUnit 指定。</summary>
    public double VideoBitrate { get; init; } = 4;
    /// <summary>自定义视频码率的输入单位。</summary>
    public BitrateUnit VideoBitrateUnit { get; init; } = BitrateUnit.Mbps;
    /// <summary>质量控制模式下传给视频编码器的质量档位。</summary>
    public EncodingQuality Quality { get; init; } = EncodingQuality.Standard;
    /// <summary>重新编码时是否沿用源音频编码格式。</summary>
    public bool KeepAudioFormat { get; init; } = true;
    /// <summary>不保留音频格式时使用的输出编码。</summary>
    public AudioFormat AudioFormat { get; init; }
    /// <summary>是否保留源音频采样率。</summary>
    public bool KeepAudioSampleRate { get; init; } = true;
    /// <summary>不保留源采样率时使用的目标采样率，单位为 Hz。</summary>
    public int AudioSampleRate { get; init; } = 48000;
    /// <summary>是否采用源音频声明的码率。</summary>
    public bool KeepAudioBitrate { get; init; } = true;
    /// <summary>不保留源音频码率时使用的目标码率，单位为 kbps。</summary>
    public int AudioBitrateKbps { get; init; } = 160;

    internal bool CopyVideo(DeinterlaceMode deinterlace) =>
        KeepVideoFormat && KeepResolution && RateMode == VideoRateMode.Bitrate && KeepVideoBitrate &&
        deinterlace != DeinterlaceMode.Always;

    internal bool CopyAudio => KeepAudioFormat && KeepAudioSampleRate && KeepAudioBitrate;

    /// <summary>检查换算为 kbps 后的视频码率是否在允许范围内。</summary>
    /// <param name="value">界面输入的码率。</param>
    /// <param name="unit">输入使用的码率单位。</param>
    /// <returns>值在 1 至 1,000,000 kbps 范围内时返回 <see langword="true" />。</returns>
    public static bool ValidVideoBitrate(double value, BitrateUnit unit)
    {
        double kbps = unit == BitrateUnit.Mbps ? value * 1000 : value;
        return kbps is >= 1 and <= 1_000_000;
    }

    internal EncodingSettings Normalize() => this with
    {
        VideoCodec = Enum.IsDefined(VideoCodec) ? VideoCodec : VideoCodec.H264,
        Resolution = Enum.IsDefined(Resolution) ? Resolution : VideoResolution.P1080,
        RateMode = Enum.IsDefined(RateMode) ? RateMode : VideoRateMode.Bitrate,
        BitrateMode = Enum.IsDefined(BitrateMode) ? BitrateMode : BitrateMode.Variable,
        Quality = Enum.IsDefined(Quality) ? Quality : EncodingQuality.Standard,
        VideoBitrate = ValidVideoBitrate(VideoBitrate, VideoBitrateUnit) ? VideoBitrate : 4,
        VideoBitrateUnit = Enum.IsDefined(VideoBitrateUnit) && ValidVideoBitrate(VideoBitrate, VideoBitrateUnit) ? VideoBitrateUnit : BitrateUnit.Mbps,
        AudioFormat = Enum.IsDefined(AudioFormat) ? AudioFormat : AudioFormat.Aac,
        AudioSampleRate = Math.Clamp(AudioSampleRate, 8000, 192000),
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 8, 1536)
    };
}

/// <summary>从 FFmpeg 的输入诊断中提取首条有效音视频轨道的参数。</summary>
/// <param name="program">只接收此节目编号下的轨道。为空时读取任一节目。</param>
internal sealed class SourceEncodingInfo(int? program = null)
{
    private static readonly Regex Program = new(@"^\s*Program (\d+)(?:\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex Stream = new(@"^\s*Stream #0:\d+.*?: (Video|Audio):\s+([\w-]+)", RegexOptions.CultureInvariant);
    private static readonly Regex Bitrate = new(@"(?:^|[ ,])([0-9]+(?:\.[0-9]+)?)\s+(kb/s|Mb/s|b/s)(?=,|\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex SampleRate = new(@"\b([0-9]+) Hz\b", RegexOptions.CultureInvariant);
    private static readonly Regex VideoSize = new(@"(?:, )([1-9][0-9]*x[1-9][0-9]*)(?=[ ,\[]|$)", RegexOptions.CultureInvariant);
    private bool _readingInput;
    private int? _currentProgram;
    public long? VideoBitrate { get; private set; }
    public string? VideoCodec { get; private set; }
    public string? VideoResolution { get; private set; }
    public long? AudioBitrate { get; private set; }
    public string? AudioCodec { get; private set; }
    public int? AudioSampleRate { get; private set; }
    public bool HasVideo => VideoCodec is not null;

    /// <summary>处理一行 FFmpeg 诊断，记录输入段内首次识别的有效音视频轨道。</summary>
    /// <param name="line">FFmpeg 标准错误流中的一行。</param>
    public void Read(string line)
    {
        if (line.StartsWith("Input #0,", StringComparison.Ordinal))
        {
            _readingInput = true;
            _currentProgram = null;
        }
        else if (line.StartsWith("Stream mapping:", StringComparison.Ordinal) || line.StartsWith("Output #", StringComparison.Ordinal))
            _readingInput = false;
        if (!_readingInput)
            return;
        if (program is int selected)
        {
            Match heading = Program.Match(line);
            if (heading.Success && int.TryParse(heading.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                _currentProgram = id;
            if (_currentProgram != selected)
                return;
        }
        // 只读取输入描述，避免把输出轨道的参数当成源参数。
        Match stream = Stream.Match(line);
        if (!stream.Success)
            return;

        if (stream.Groups[1].Value == "Video")
        {
            if (HasVideo || line.Contains("(attached pic)", StringComparison.Ordinal))
                return;
            VideoCodec = stream.Groups[2].Value;
            Match size = VideoSize.Match(line);
            VideoResolution = size.Success ? size.Groups[1].Value : null;
            VideoBitrate = ParseBitrate(line);
        }
        else if (AudioCodec is null)
        {
            AudioCodec = stream.Groups[2].Value;
            AudioBitrate = ParseBitrate(line);
            Match rate = SampleRate.Match(line);
            if (int.TryParse(rate.Groups[1].Value, CultureInfo.InvariantCulture, out int value))
                AudioSampleRate = value;
        }
    }

    private static long? ParseBitrate(string line)
    {
        Match rate = Bitrate.Match(line);
        if (!rate.Success || !double.TryParse(rate.Groups[1].Value, CultureInfo.InvariantCulture, out double value))
            return null;
        double bits = value * (rate.Groups[2].Value switch { "Mb/s" => 1_000_000, "kb/s" => 1000, _ => 1 });
        return bits is > 0 and <= int.MaxValue ? (long)Math.Round(bits) : null;
    }
}

/// <summary>将编码选项和已探测的轨道参数转换成 FFmpeg 参数。</summary>
internal static class EncodingArguments
{
    /// <summary>根据源轨道及设置生成 FFmpeg 的复制、过滤和编码参数。</summary>
    /// <param name="encoder">视频编码器实现。</param>
    /// <param name="deinterlace">应用于视频轨道的去隔行模式。</param>
    /// <param name="settings">本次任务的编码设置。</param>
    /// <param name="source">探测得到的轨道信息。</param>
    /// <returns>按 FFmpeg 参数顺序排列的参数数组，只处理已发现的轨道。</returns>
    /// <exception cref="InvalidOperationException">无法保留源编码格式，或所需的原始码率、采样率等不可用。</exception>
    public static string[] Create(VideoEncoder encoder, DeinterlaceMode deinterlace, EncodingSettings settings, SourceEncodingInfo source)
    {
        var args = new List<string>();
        if (source.HasVideo)
        {
            if (settings.CopyVideo(deinterlace))
                args.AddRange(["-c:v", "copy"]);
            else
                AddVideo(args, encoder, deinterlace, settings, source);
        }
        if (source.AudioCodec is not null)
        {
            if (settings.CopyAudio)
                args.AddRange(["-c:a", "copy"]);
            else
                AddAudio(args, settings, source);
        }
        return args.ToArray();
    }

    /// <summary>按用户设置对无法直接封装的轨道重新编码。</summary>
    /// <param name="encoder">视频编码器。</param>
    /// <param name="deinterlace">视频去隔行设置。</param>
    /// <param name="settings">编码设置。</param>
    /// <param name="source">输入轨道的原始编码参数。</param>
    /// <param name="convertVideo">是否必须重新编码视频。</param>
    /// <param name="convertAudio">是否必须重新编码音频。</param>
    /// <returns>先复制所有轨道，再覆盖无法封装的轨道的 FFmpeg 参数。</returns>
    public static string[] CreateRecordingFallback(VideoEncoder encoder, DeinterlaceMode deinterlace, EncodingSettings settings,
        SourceEncodingInfo source, bool convertVideo, bool convertAudio)
    {
        var args = new List<string> { "-c", "copy" };
        if (convertVideo)
            AddVideo(args, encoder, deinterlace, settings, source);
        if (convertAudio)
            AddAudio(args, settings, source);
        return args.ToArray();
    }

    internal static string? OutputAudioFormat(EncodingSettings settings, SourceEncodingInfo source) =>
        settings.KeepAudioFormat ? source.AudioCodec : settings.AudioFormat switch
        {
            AudioFormat.Mp3 => "mp3", AudioFormat.Ac3 => "ac3", AudioFormat.Opus => "opus", _ => "aac"
        };

    private static void AddVideo(List<string> args, VideoEncoder encoder, DeinterlaceMode deinterlace, EncodingSettings settings, SourceEncodingInfo source)
    {
        string family = settings.KeepVideoFormat ? source.VideoCodec! : settings.VideoCodec == VideoCodec.H265 ? "hevc" : "h264";
        bool avcOrHevc = family is "h264" or "hevc";
        if (!avcOrHevc && encoder != VideoEncoder.Software)
            throw new InvalidOperationException($"保留原视频格式（{family}）进行转码时，请选择软件编码，或关闭「保持原格式」并指定视频格式。");
        string codec = (encoder, family) switch
        {
            (VideoEncoder.Software, "h264") => "libx264",
            (VideoEncoder.Software, "hevc") => "libx265",
            (VideoEncoder.Software, "vp9") => "libvpx-vp9",
            (VideoEncoder.Software, "av1") => "libaom-av1",
            (VideoEncoder.Software, "mpeg2video" or "mpeg4") => family,
            (VideoEncoder.Intel, _) => family + "_qsv",
            (VideoEncoder.Nvidia, _) => family + "_nvenc",
            (VideoEncoder.Amd, _) => family + "_amf",
            _ => throw new InvalidOperationException($"无法在转码时保留原视频格式（{family}）。请关闭「保持原格式」并指定视频格式。")
        };
        var filters = new List<string>();
        if (deinterlace != DeinterlaceMode.Off)
            filters.Add(deinterlace == DeinterlaceMode.Always ? "bwdif=mode=send_frame:parity=auto:deint=all"
                : "bwdif=mode=send_frame:parity=auto:deint=interlaced");
        if (!settings.KeepResolution)
        {
            (int width, int height) = settings.Resolution switch
            {
                VideoResolution.P360 => (640, 360), VideoResolution.P480 => (854, 480), VideoResolution.P720 => (1280, 720),
                VideoResolution.P1440 => (2560, 1440), VideoResolution.P2160 => (3840, 2160), _ => (1920, 1080)
            };
            // Fit the displayed picture in the square-pixel canvas, including anamorphic sources.
            filters.Add(FormattableString.Invariant(
                $"scale='max(2,2*round(min({width},{height}*dar)/2))':'max(2,2*round(min({height},{width}/dar)/2))'"));
            filters.Add(FormattableString.Invariant($"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1"));
        }
        if (filters.Count > 0)
            args.AddRange(["-vf", string.Join(',', filters)]);
        args.AddRange(["-pix_fmt", encoder == VideoEncoder.Intel ? "nv12" : "yuv420p", "-c:v", codec]);

        // 同一质量档位在不同编码器上使用各自的参数和标度。
        if (settings.RateMode == VideoRateMode.Quality)
        {
            if (family is "mpeg2video" or "mpeg4")
            {
                args.AddRange(["-q:v", settings.Quality switch { EncodingQuality.Smaller => "7", EncodingQuality.Higher => "2", _ => "4" }]);
                return;
            }
            int standard = family switch { "hevc" => 28, "vp9" => 31, "av1" => 32, _ => 23 };
            int quality = standard + (settings.Quality switch { EncodingQuality.Smaller => 5, EncodingQuality.Higher => -5, _ => 0 });
            string value = quality.ToString(CultureInfo.InvariantCulture);
            args.AddRange(encoder switch
            {
                VideoEncoder.Intel => ["-global_quality", value],
                VideoEncoder.Nvidia => ["-rc", "vbr", "-cq", value, "-b:v", "0"],
                VideoEncoder.Amd => ["-rc", "cqp", "-qp_i", value, "-qp_p", value],
                _ when family is "vp9" or "av1" => ["-crf", value, "-b:v", "0"],
                _ => ["-crf", value]
            });
            return;
        }

        long bits = settings.KeepVideoBitrate
            ? RequireBitrate(source.VideoBitrate, "视频")
            : (long)Math.Round(settings.VideoBitrate * (settings.VideoBitrateUnit == BitrateUnit.Mbps ? 1_000_000 : 1000));
        string rate = bits.ToString(CultureInfo.InvariantCulture);
        bool constant = !settings.KeepVideoBitrate && settings.BitrateMode == BitrateMode.Constant;
        args.AddRange(["-b:v", rate]);
        if (encoder == VideoEncoder.Nvidia)
            args.AddRange(["-rc", constant ? "cbr" : "vbr"]);
        if (encoder == VideoEncoder.Amd)
            args.AddRange(["-rc", constant ? "cbr" : "vbr_peak"]);
        if (constant)
        {
            args.AddRange(["-minrate", rate, "-maxrate", rate, "-bufsize", (bits * 2).ToString(CultureInfo.InvariantCulture)]);
            if (encoder == VideoEncoder.Software && avcOrHevc)
                args.AddRange(family == "hevc" ? ["-x265-params", "strict-cbr=1"] : ["-x264-params", "filler=1"]);
        }
        else if (encoder is VideoEncoder.Intel or VideoEncoder.Amd)
        {
            args.AddRange(["-maxrate", (bits * 2).ToString(CultureInfo.InvariantCulture),
                "-bufsize", (bits * 4).ToString(CultureInfo.InvariantCulture)]);
        }
    }

    private static void AddAudio(List<string> args, EncodingSettings settings, SourceEncodingInfo source)
    {
        string codec = OutputAudioFormat(settings, source) switch
        {
            "mp3" => "libmp3lame", "opus" => "libopus", "vorbis" => "libvorbis", "mp2" => "mp2",
            { } original => original,
            _ => throw new InvalidOperationException("无法确定原音频格式。请选择输出音频格式。")
        };
        int sampleRate = settings.KeepAudioSampleRate ? source.AudioSampleRate ??
            throw new InvalidOperationException("无法确定原音频采样率。请关闭「保持原采样率」并指定采样率。")
            : settings.AudioSampleRate;
        long bitrate = settings.KeepAudioBitrate ? RequireBitrate(source.AudioBitrate, "音频") : settings.AudioBitrateKbps * 1000L;
        args.AddRange(["-c:a", codec, "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-b:a", bitrate.ToString(CultureInfo.InvariantCulture)]);
    }

    private static long RequireBitrate(long? bitrate, string stream) => bitrate ??
        throw new InvalidOperationException($"无法确定原{stream}码率。请关闭「保持原码率」并指定{stream}码率。");
}
