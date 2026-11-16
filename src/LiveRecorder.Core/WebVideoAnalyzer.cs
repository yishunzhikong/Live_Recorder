using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LiveRecorder.Core;

public sealed record WebVideoLink(string Label, string Url);
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
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (string argument in new[]
        {
            "--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-playlist", "--playlist-items", "1",
            "--skip-download", "--dump-single-json", "--encoding", "utf-8", "--socket-timeout", "15",
            "--retries", "1", "--extractor-retries", "1", "--js-runtimes", "deno:" + deno, "--", address
        })
            process.StartInfo.ArgumentList.Add(argument);

        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("无法启动内置 yt-dlp，请重新完整解压发布包。", ex); }

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string message = (await errors.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(message.Length == 0 ? "yt-dlp 无法分析此网页。"
                    : message[..Math.Min(message.Length, 1800)]);
            }
            return Parse(await output.ConfigureAwait(false));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) when (process.HasExited) { }
            await Task.WhenAll(output, errors).ConfigureAwait(false);
        }
    }

    private static WebVideoAnalysis Parse(string result)
    {
        using JsonDocument document = JsonDocument.Parse(result);
        JsonElement media = document.RootElement;
        while (media.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
        {
            media = entries.EnumerateArray().FirstOrDefault(entry => entry.ValueKind == JsonValueKind.Object);
            if (media.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("网页中没有可分析的视频。");
        }

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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        WebVideoLink[] links = candidates.AsEnumerable().Reverse().OrderBy(item => item.Order)
            .Where(item => seen.Add(item.Link.Url)).Select(item => item.Link).ToArray();
        return new WebVideoAnalysis(StringProperty(media, "title") ?? "视频", links);

        void AddFormat(JsonElement format)
        {
            if (format.ValueKind != JsonValueKind.Object ||
                StringProperty(format, "url") is not { } url || !IsHttpUrl(url) ||
                (format.TryGetProperty("has_drm", out JsonElement drm) && drm.ValueKind == JsonValueKind.True))
                return;
            string? video = StringProperty(format, "vcodec");
            string? audio = StringProperty(format, "acodec");
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
            string label = string.Join(" · ", new[] { role, resolution, StringProperty(format, "ext")?.ToUpperInvariant(),
                StringProperty(format, "format_id") }.OfType<string>());
            candidates.Add((new WebVideoLink(label, url), order));
        }
    }

    private static string? StringProperty(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme is "http" or "https") && !string.IsNullOrEmpty(uri.Host);
}
