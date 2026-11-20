using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LiveRecorder.Core;

/// <summary>描述 yt-dlp 解析出的一个可选媒体格式。</summary>
/// <param name="Label">用于格式列表的显示名称。</param>
/// <param name="Url">解析得到的 HTTP(S) 媒体直链。</param>
/// <param name="AudioOnly">是否只包含音轨。</param>
/// <param name="PageUrl">用于重新提取格式的原网页地址。</param>
/// <param name="FormatId">yt-dlp 返回的格式标识。</param>
/// <param name="IsLive">解析时媒体是否为直播。</param>
public sealed record WebVideoLink(string Label, string Url, bool AudioOnly, string PageUrl, string? FormatId, bool IsLive)
{
    /// <summary>访问该媒体地址时允许转发的请求头。没有声明时为空。</summary>
    public MediaRequestHeaders? Headers { get; init; }

    /// <summary>音轨重新提取时沿用的浏览器名称。为空时不读取浏览器 Cookie。</summary>
    internal string? CookieBrowser { get; init; }
}

/// <summary>表示一次网页媒体分析得到的标题和格式列表。</summary>
/// <param name="Title">页面媒体标题。</param>
/// <param name="Links">通过筛选的可播放或可下载格式。</param>
public sealed record WebVideoAnalysis(string Title, IReadOnlyList<WebVideoLink> Links);

/// <summary>调用 yt-dlp 解析网页格式，并为所选音轨提供下载和预览。</summary>
/// <param name="executable">yt-dlp 可执行文件路径。</param>
/// <param name="deno">提供 JavaScript 运行环境的 Deno 可执行文件路径。</param>
/// <param name="cacheDirectory">便携工具缓存目录。</param>
/// <param name="debug">用于记录格式提取和音频供流诊断的可选记录器。</param>
public sealed class WebVideoAnalyzer(string? executable, string deno, string cacheDirectory, DebugTrace? debug = null)
{
    /// <summary>从网页提取用于预览或下载的 HTTP(S) 直链。</summary>
    /// <param name="address">包含媒体的网页地址。</param>
    /// <param name="token">用于中止分析的取消令牌。</param>
    /// <param name="cookieBrowser">yt-dlp 读取 Cookie 的浏览器名称。为空时不读取。</param>
    /// <returns>页面标题和可选格式列表。</returns>
    /// <exception cref="ArgumentException">输入不是有效的 HTTP(S) 地址。</exception>
    /// <exception cref="InvalidDataException">未得到可用的媒体直链。</exception>
    public async Task<WebVideoAnalysis> AnalyzeAsync(string address, CancellationToken token, string? cookieBrowser = null)
    {
        address = address.Trim();
        if (!IsHttpUrl(address))
            throw new ArgumentException("请输入完整的 HTTP 或 HTTPS 网页视频链接。");
        token.ThrowIfCancellationRequested();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        using var process = new Process
        {
            StartInfo = CreateStartInfo(["--skip-download", "--dump-single-json"], address, cookieBrowser)
        };
        Start(process);
        long started = Stopwatch.GetTimestamp();
        debug?.Write("yt-dlp", $"开始提取网页媒体格式 · 子进程 PID={process.Id} · Deno 可用={File.Exists(deno)} · 浏览器 Cookie={cookieBrowser is not null} · 超时=75 秒。");

        Task<JsonDocument> output = JsonDocument.ParseAsync(process.StandardOutput.BaseStream, cancellationToken: timeout.Token);
        Task<string> errors = ToolProcess.ReadDiagnosticTailAsync(process.StandardError);
        try
        {
            await WaitForSuccessAsync(process, errors, "yt-dlp 无法分析此网页。", timeout.Token).ConfigureAwait(false);
            WebVideoAnalysis analysis = Parse((await output.ConfigureAwait(false)).RootElement, address, cookieBrowser);
            if (debug?.Enabled == true)
                debug.Write("yt-dlp", $"格式提取完成 · 格式数={analysis.Links.Count} · 音频格式数={analysis.Links.Count(link => link.AudioOnly)} · 含直播格式={analysis.Links.Any(link => link.IsLive)} · 耗时={Stopwatch.GetElapsedTime(started).TotalSeconds:F2} 秒。");
            return analysis;
        }
        finally
        {
            ToolProcess.Stop(process);
            try { (await output.ConfigureAwait(false)).Dispose(); }
            catch (JsonException) { /* yt-dlp errors may leave stdout empty or incomplete. */ }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            await errors.ConfigureAwait(false);
            if (debug?.Enabled == true && process.HasExited)
                debug.Write("yt-dlp", $"格式提取子进程已退出 · 状态码={process.ExitCode} · 已取消={token.IsCancellationRequested} · 已超时={timeout.IsCancellationRequested && !token.IsCancellationRequested}。");
        }
    }

    /// <summary>重新提取所选音轨并将它下载为 M4A。</summary>
    /// <param name="link">分析时选中的纯音频格式。</param>
    /// <param name="directory">用于存放下载输出的目录。</param>
    /// <param name="ffmpeg">FFmpeg 可执行文件路径。</param>
    /// <param name="token">用于停止下载的取消令牌。</param>
    /// <returns>生成的 M4A 文件路径。</returns>
    /// <exception cref="InvalidDataException">下载结束时没有生成非空 M4A 文件。</exception>
    public async Task<string> DownloadAudioAsync(WebVideoLink link, string directory, string? ffmpeg, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                [.. AudioOptions(link, ToolProcess.RequireExecutable(ffmpeg, "FFmpeg")), "--extract-audio", "--audio-format", "m4a",
                    "--output", "audio.%(ext)s", "--quiet"], link.PageUrl, link.CookieBrowser, Path.GetFullPath(directory))
        };
        Start(process);

        debug?.Write("yt-dlp", $"开始下载网页音轨 · 子进程 PID={process.Id} · 直播={link.IsLive} · 输出=M4A。");
        Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task<string> errors = ToolProcess.ReadDiagnosticTailAsync(process.StandardError);
        try
        {
            await WaitForSuccessAsync(process, errors, "yt-dlp 无法下载所选音频。", token).ConfigureAwait(false);
            string path = Path.Combine(process.StartInfo.WorkingDirectory, "audio.m4a");
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException("yt-dlp 没有生成可播放的 M4A 音频。");
            token.ThrowIfCancellationRequested();
            if (debug?.Enabled == true)
                debug.Write("yt-dlp", $"网页音轨下载结束 · 状态码={process.ExitCode} · 字节={new FileInfo(path).Length}。");
            return path;
        }
        finally
        {
            ToolProcess.Stop(process);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
        }
    }

    /// <summary>重新提取所选音轨，并将音频通过管道提供给预览播放器。</summary>
    /// <param name="link">分析时选中的纯音频格式。</param>
    /// <param name="ffmpeg">FFmpeg 可执行文件路径。</param>
    /// <param name="token">用于停止预览供流的取消令牌。</param>
    /// <returns>带进程监控的预览流。调用方负责异步释放。</returns>
    public PreviewStream OpenAudioPreview(WebVideoLink link, string? ffmpeg, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string executable = ToolProcess.RequireExecutable(ffmpeg, "FFmpeg");
        string inputArgs = link.IsLive ? "-rw_timeout 15000000" : "-rw_timeout 15000000 -re";
        ProcessStartInfo start = CreateStartInfo(
            [.. AudioOptions(link, executable), "--output", "-", "--no-part", "--quiet",
                // Let FFmpeg seek over HTTP when a file puts its index at the end.
                "--downloader", "ffmpeg",
                "--downloader-args", $"ffmpeg_i:{inputArgs}",
                // A transport stream carries a clock for VLC when the input pipe cannot seek.
                "--downloader-args", "ffmpeg_o:-vn -c:a aac -b:a 160k -f mpegts -muxdelay 0.1 -flush_packets 1"],
            link.PageUrl, link.CookieBrowser);
        start.Environment["PATH"] = Path.GetDirectoryName(executable) + Path.PathSeparator
            + Environment.GetEnvironmentVariable("PATH");
        debug?.Write("yt-dlp", $"准备网页音轨预览 · 直播={link.IsLive} · 管道=MPEG-TS/AAC。");
        return PreviewStream.Start(start, "yt-dlp 无法在线播放所选音频。", token, debug);
    }

    // 用解析时选中的格式 ID 重新从原网页取流，避免取到其他音轨。
    private static string[] AudioOptions(WebVideoLink link, string ffmpeg)
    {
        if (!link.AudioOnly || string.IsNullOrWhiteSpace(link.FormatId) || !IsHttpUrl(link.PageUrl))
            throw new ArgumentException("请重新分析网页并选择有效的音频格式。");
        string formatId = link.FormatId.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        return ["--format", $"best*[format_id='{formatId}']", "--no-simulate", "--abort-on-unavailable-fragments",
            "--ffmpeg-location", ffmpeg];
    }

    /// <summary>构造 yt-dlp 进程参数，并按需让 yt-dlp 读取所选浏览器的 Cookie。</summary>
    /// <param name="options">当前操作使用的参数。</param>
    /// <param name="address">要提取的网页地址。</param>
    /// <param name="cookieBrowser">浏览器名称。为空时省略 Cookie 参数。</param>
    /// <param name="directory">可选的进程工作目录。</param>
    /// <returns>可直接用于启动进程的配置。</returns>
    private ProcessStartInfo CreateStartInfo(string[] options, string address, string? cookieBrowser, string? directory = null)
    {
        ProcessStartInfo start = ToolProcess.CreateStartInfo(RequireExecutable(), cacheDirectory, directory);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardErrorEncoding = Encoding.UTF8;
        string[] cookieOptions = cookieBrowser is null ? [] : ["--cookies-from-browser", cookieBrowser];
        string[] arguments =
        [
            "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--playlist-items", "1",
            "--encoding", "utf-8", "--socket-timeout", "15", "--retries", "1", "--extractor-retries", "1",
            "--js-runtimes", "deno:" + deno, .. cookieOptions, .. options, "--", address
        ];
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    internal string RequireExecutable()
    {
        string ytDlp = ToolProcess.RequireExecutable(executable, "yt-dlp");
        if (!File.Exists(deno))
            throw new InvalidOperationException("缺少 Deno。请在「工具」菜单中选择「安装 / 升级 yt-dlp」。");
        return ytDlp;
    }

    private static void Start(Process process)
    {
        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("无法启动 yt-dlp，请在「工具」菜单中检查安装。", ex); }
    }

    private static async Task WaitForSuccessAsync(Process process, Task<string> errors, string fallback, CancellationToken token)
    {
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode == 0)
            return;
        string message = await errors.ConfigureAwait(false);
        throw new InvalidOperationException(message.Length == 0 ? fallback : message);
    }

    private static WebVideoAnalysis Parse(JsonElement media, string address, string? cookieBrowser)
    {
        while (media.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
        {
            media = entries.EnumerateArray().FirstOrDefault(entry => entry.ValueKind == JsonValueKind.Object);
            if (media.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("网页中没有可分析的视频。");
        }

        string? page = StringProperty(media, "webpage_url");
        string pageUrl = page is not null && IsHttpUrl(page) ? page : address;
        bool isLive = media.TryGetProperty("is_live", out JsonElement live) && live.ValueKind == JsonValueKind.True;
        var candidates = new List<(WebVideoLink Link, int Order)>();
        if (media.TryGetProperty("formats", out JsonElement formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement format in formats.EnumerateArray())
                AddFormat(format);
        }
        if (candidates.Count == 0)
            AddFormat(media);
        if (candidates.Count == 0)
            throw new InvalidDataException("yt-dlp 没有返回可用的 HTTP(S) 媒体直链。");

        // 视频按直链去重。音轨保留格式 ID，以便下载或预览时精确选轨。
        var seen = new HashSet<(string Url, string? FormatId)>();
        WebVideoLink[] links = candidates.AsEnumerable().Reverse().OrderBy(item => item.Order)
            .Where(item => seen.Add((item.Link.Url, item.Link.AudioOnly ? item.Link.FormatId : null)))
            .Select(item => item.Link).ToArray();
        return new WebVideoAnalysis(StringProperty(media, "title") ?? "视频", links);

        void AddFormat(JsonElement format)
        {
            if (format.ValueKind != JsonValueKind.Object ||
                StringProperty(format, "url") is not { } url || !IsHttpUrl(url) ||
                (format.TryGetProperty("has_drm", out JsonElement drm) && drm.ValueKind == JsonValueKind.True))
                return;
            string? video = StringProperty(format, "vcodec");
            string? audio = StringProperty(format, "acodec");
            string? extension = StringProperty(format, "ext")?.ToLowerInvariant();
            if (video is null && extension is "aac" or "flac" or "m4a" or "mp3" or "oga" or "opus" or "wav" or "weba")
                video = "none";
            if ((video == "none" && audio == "none") || StringProperty(format, "protocol") == "mhtml")
                return;

            (string role, int order) = (video, audio) switch
            {
                ("none", _) => ("音频", 2),
                (_, "none") => ("仅视频", 1),
                (not null, not null) => ("视频 + 音频", 0),
                _ => ("媒体", 0)
            };
            string? resolution = format.TryGetProperty("height", out JsonElement height) && height.ValueKind == JsonValueKind.Number
                ? $"{height.GetRawText()}p" : StringProperty(format, "format_note");
            string? formatId = StringProperty(format, "format_id");
            string label = string.Join(" · ", new[] { role, resolution, extension?.ToUpperInvariant(), formatId }.OfType<string>());
            candidates.Add((new WebVideoLink(label, url, video == "none", pageUrl, formatId, isLive)
            {
                Headers = MediaRequestHeaders.FromExtractor(ReadHeaders(format), ReadHeaders(media)),
                CookieBrowser = cookieBrowser
            }, order));
        }
    }

    private static JsonElement ReadHeaders(JsonElement item) =>
        item.TryGetProperty("http_headers", out JsonElement headers) ? headers : default;

    private static string? StringProperty(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme is "http" or "https") && !string.IsNullOrEmpty(uri.Host);
}
