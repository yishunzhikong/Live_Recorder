using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LiveRecorder.Core;

public sealed record WebVideoLink(string Label, string Url, bool AudioOnly, string PageUrl, string? FormatId);
public sealed record WebVideoAnalysis(string Title, IReadOnlyList<WebVideoLink> Links);

public sealed class WebVideoAnalyzer(string executable, string deno)
{
    public async Task<WebVideoAnalysis> AnalyzeAsync(string address, CancellationToken token)
    {
        address = address.Trim();
        if (!IsHttpUrl(address))
            throw new ArgumentException("请输入完整的 HTTP 或 HTTPS 网页视频链接。");
        token.ThrowIfCancellationRequested();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        using var process = CreateProcess(["--skip-download", "--dump-single-json"], address);
        Start(process);

        Task<JsonDocument> output = JsonDocument.ParseAsync(process.StandardOutput.BaseStream, cancellationToken: timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            await WaitForSuccessAsync(process, errors, "yt-dlp 无法分析此网页。", timeout.Token).ConfigureAwait(false);
            return Parse((await output.ConfigureAwait(false)).RootElement, address);
        }
        finally
        {
            Stop(process);
            try { (await output.ConfigureAwait(false)).Dispose(); }
            catch (JsonException) { /* yt-dlp errors may leave stdout empty or incomplete. */ }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            await errors.ConfigureAwait(false);
        }
    }

    public async Task<string> DownloadAudioAsync(WebVideoLink link, string directory, string ffmpeg, CancellationToken token)
    {
        if (!link.AudioOnly || string.IsNullOrWhiteSpace(link.FormatId) || !IsHttpUrl(link.PageUrl))
            throw new ArgumentException("请重新分析网页并选择有效的音频格式。");
        token.ThrowIfCancellationRequested();
        string formatId = link.FormatId.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        using var process = CreateProcess(
            ["--format", $"best*[format_id='{formatId}']", "--no-simulate", "--abort-on-unavailable-fragments",
                "--extract-audio", "--audio-format", "m4a", "--ffmpeg-location", ffmpeg,
                "--output", "audio.%(ext)s", "--quiet"],
            link.PageUrl, Path.GetFullPath(directory));
        Start(process);

        Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            await WaitForSuccessAsync(process, errors, "yt-dlp 无法下载所选音频。", token).ConfigureAwait(false);
            string path = Path.Combine(process.StartInfo.WorkingDirectory, "audio.m4a");
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException("yt-dlp 没有生成可播放的 M4A 音频。");
            token.ThrowIfCancellationRequested();
            return path;
        }
        finally
        {
            Stop(process);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
        }
    }

    private Process CreateProcess(string[] options, string address, string? directory = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory ?? "",
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        string[] arguments =
        [
            "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--playlist-items", "1",
            "--encoding", "utf-8", "--socket-timeout", "15", "--retries", "1", "--extractor-retries", "1",
            "--js-runtimes", "deno:" + deno, .. options, "--", address
        ];
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        return process;
    }

    private static void Start(Process process)
    {
        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("无法启动内置 yt-dlp，请重新完整解压发布包。", ex); }
    }

    private static async Task WaitForSuccessAsync(Process process, Task<string> errors, string fallback, CancellationToken token)
    {
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode == 0)
            return;
        string message = (await errors.ConfigureAwait(false)).Trim();
        throw new InvalidOperationException(message.Length == 0 ? fallback : message[..Math.Min(message.Length, 1800)]);
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) when (process.HasExited) { }
    }

    private static WebVideoAnalysis Parse(JsonElement media, string address)
    {
        while (media.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
        {
            media = entries.EnumerateArray().FirstOrDefault(entry => entry.ValueKind == JsonValueKind.Object);
            if (media.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("网页中没有可分析的视频。");
        }

        string? page = StringProperty(media, "webpage_url");
        string pageUrl = page is not null && IsHttpUrl(page) ? page : address;
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
            candidates.Add((new WebVideoLink(label, url, video == "none", pageUrl, formatId), order));
        }
    }

    private static string? StringProperty(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme is "http" or "https") && !string.IsNullOrEmpty(uri.Host);
}
