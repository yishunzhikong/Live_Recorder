using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

public sealed class CctvLinkResolver(HttpClient? http = null) : ICctvLinkResolver
{
    private const int PageSize = 100;
    private static readonly Uri Site = new("https://tv.cctv.com/");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string[] Domains = ["cctv.com", "cctv.cn", "cntv.cn", "ipanda.com"];
    private static readonly Regex AlbumCode = Pattern(@"VID[AB][A-Za-z0-9]+");
    private static readonly Regex EpisodeCode = Pattern(@"VIDE[A-Za-z0-9]+");
    private static readonly Regex VideoId = Pattern(@"^[0-9a-f]{32}$");
    private static readonly Regex PageVideoId = Pattern("""\b(?:guid|guid_0|videoCenterId|data-guid|data-pid)\s*["']?\s*[:=]\s*["']([0-9a-f]{32})["']""");
    private static readonly Regex EmbeddedVideoId = Pattern("""(?:\\u002fvideo\\u002f"?|\[!--begin:htmlVideoCode--\])([0-9a-f]{32})(?=["',])""");
    private static readonly Regex PlaylistVideoId = Pattern(@"/asp/(?:[^/]+/)?hls/[^/]+/[^/]+/[^/]+/default/([0-9a-f]{32})/[^/]+\.m3u8$");
    private static readonly Regex PageTitle = Pattern(@"<title\b[^>]*>(.*?)</title>", RegexOptions.Singleline);
    private static readonly Regex PageAlbumCodes = Pattern("""\bvideotvCodes\s*=\s*["']([^"']+)["']""");
    private static readonly Regex EpisodeAnchor = Pattern("""<a\b(?<attributes>[^>]*)>(?<text>[^<]*)</a\s*>""");
    private static readonly Regex Href = Pattern("""\bhref\s*=\s*["']([^"']+)["']""");
    private static readonly Regex AnchorTitle = Pattern("""\btitle\s*=\s*["']([^"']+)["']""");
    private static readonly Regex VariantCode = Pattern(@"/hls/(\d+)/");

    public async Task<IReadOnlyList<CctvEpisode>> GetEpisodesAsync(Uri page, bool includeAlbum, CancellationToken token)
    {
        if (!IsHttp(page))
            throw new ArgumentException("请输入完整的 CCTV 网页链接。");
        if (IsPlaylist(page))
            return [new CctvEpisode("HLS 播放列表", page)];
        if (!IsCctvPage(page))
            throw new ArgumentException("请输入央视网节目、专辑或 HLS 播放列表链接。");

        string album = AlbumCode.Match(page.AbsolutePath).Value;
        if (album.Length > 0)
            return await GetAlbumAsync(album, page, token).ConfigureAwait(false);

        (string html, Uri finalPage) = await GetTextAsync(page, Site, token).ConfigureAwait(false);
        album = AlbumCode.Match(finalPage.AbsolutePath).Value;
        if (album.Length > 0)
            return await GetAlbumAsync(album, finalPage, token, html).ConfigureAwait(false);

        string guid = ExtractVideoId(html);
        if (guid.Length == 0)
            throw new InvalidDataException("页面里没有找到 CCTV 视频编号。请使用具体节目页或专辑页。");
        string title = Decode(Group(PageTitle, html));
        var current = new CctvEpisode(title.Length == 0 ? "CCTV 节目" : title, finalPage, guid);
        if (!includeAlbum)
            return [current];

        album = AlbumCode.Matches(Group(PageAlbumCodes, html)).LastOrDefault()?.Value ?? "";
        if (album.Length == 0)
        {
            foreach (string service in new[] { "tvcctv", "cctv4k" })
            {
                var metadata = new Uri($"https://api.cntv.cn/video/videoinfoByGuid?serviceId={service}&guid={guid}");
                using JsonDocument info = await GetJsonAsync(metadata, Site, token).ConfigureAwait(false);
                album = AlbumCode.Match(Value(info.RootElement, "album_id")).Value;
                if (album.Length > 0)
                    break;
            }
        }
        return album.Length == 0 ? [current] : await GetAlbumAsync(album, finalPage, token, current: current).ConfigureAwait(false);
    }

    public async Task<CctvMediaLink> ResolveAsync(CctvEpisode episode, CctvQuality quality, CancellationToken token)
    {
        Uri? direct = IsPlaylist(episode.Page) ? episode.Page : null;
        string guid = direct is null ? episode.VideoId ?? "" : Group(PlaylistVideoId, direct.AbsolutePath);
        if (direct is not null && guid.Length == 0)
        {
            (string text, Uri address) = await GetTextAsync(direct, Site, token).ConfigureAwait(false);
            if (!IsHls(text))
                throw new InvalidDataException("服务器未返回有效的 HLS 播放列表。");
            return new CctvMediaLink(address, null, []);
        }
        if (!VideoId.IsMatch(guid))
        {
            if (!IsCctvPage(episode.Page))
                throw new ArgumentException("无效的 CCTV 节目链接。");
            (string html, _) = await GetTextAsync(episode.Page, Site, token).ConfigureAwait(false);
            guid = ExtractVideoId(html);
        }
        if (!VideoId.IsMatch(guid))
            throw new InvalidDataException("未找到这集节目的 CCTV 视频编号。");

        var endpoint = new Uri($"https://vdn.apps.cntv.cn/api/getHttpVideoInfo.do?pid={guid}");
        using JsonDocument info = await GetJsonAsync(endpoint, Site, token).ConfigureAwait(false);
        JsonElement video = info.RootElement;
        if (Value(video, "is_invalid_copyright") == "1" || Value(video, "is_protected") == "1" || Value(video, "is_preview") == "1")
            throw new InvalidDataException("央视接口没有提供这集节目的完整公开播放权限。");

        string referenceUrl = Value(Property(video, "manifest"), "hls_enc2_url");
        var indexes = await Task.WhenAll(ReadPlaylistAsync(referenceUrl, token),
            ReadPlaylistAsync(Value(video, "hls_url"), token)).ConfigureAwait(false);
        Dictionary<int, Uri> listed = indexes[0] is { } reference ? ReadVariants(reference.Text, reference.Address) : [];
        Dictionary<int, Uri> candidates = [];
        if (indexes[1] is { } source)
        {
            candidates = ReadVariants(source.Text, source.Address);
            if (source.Text.Contains("#EXTINF:", StringComparison.Ordinal) && GetCode(source.Address) is int code)
                candidates.TryAdd(code, source.Address);
        }
        CctvQuality[] listedQualities = CctvQuality.All
            .Where(level => listed.ContainsKey(level.Code) || candidates.ContainsKey(level.Code)).ToArray();
        // A CCTV direct link already identifies a grade. As in the reference, start at
        // the higher of that grade and the requested grade.
        int startCode = Math.Max(quality.Code, direct is null ? 0 : GetCode(direct) ?? 0);
        foreach (CctvQuality level in CctvQuality.All.Where(level => level.Code <= startCode))
        {
            if (!candidates.TryGetValue(level.Code, out Uri? stream))
                continue;
            var media = await ReadPlaylistAsync(stream.AbsoluteUri, token).ConfigureAwait(false);
            if (media is { } checkedMedia && checkedMedia.Text.Contains("#EXTINF:", StringComparison.Ordinal) &&
                GetCode(checkedMedia.Address) == level.Code)
                return new CctvMediaLink(checkedMedia.Address, level, listedQualities);
        }
        string grades = string.Join("、", listedQualities.Select(level => level.Label));
        throw new InvalidDataException(grades.Length == 0
            ? "央视接口没有返回可播放的清晰度档位。"
            : $"央视清单列出 {grades}。当前没有可直接预览或下载的公开播放源。");
    }

    private async Task<IReadOnlyList<CctvEpisode>> GetAlbumAsync(string album, Uri page, CancellationToken token,
        string? html = null, CctvEpisode? current = null)
    {
        var episodes = new List<CctvEpisode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string filter in new[] { "&pub=1&mode=0", "" })
        {
            for (int number = 1; ; number++)
            {
                var endpoint = new Uri($"https://api.cntv.cn/NewVideo/getVideoListByAlbumIdNew?id={album}&serviceId=tvcctv&sort=asc{filter}&p={number}&n={PageSize}");
                using JsonDocument info = await GetJsonAsync(endpoint, Site, token).ConfigureAwait(false);
                JsonElement data = Property(info.RootElement, "data");
                JsonElement entries = Property(data, "list");
                if (entries.ValueKind != JsonValueKind.Array)
                    break;

                int before = episodes.Count;
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    string guid = Value(entry, "guid");
                    string title = Decode(Value(entry, "title"));
                    bool hasId = VideoId.IsMatch(guid);
                    bool hasPage = TryHttpUri(page, Value(entry, "url"), out Uri? episodePage) && IsCctvPage(episodePage!);
                    if (!hasId && !hasPage)
                        continue;
                    string key = hasId ? guid : episodePage!.AbsolutePath;
                    if (seen.Add(key))
                        episodes.Add(new CctvEpisode(title.Length == 0 ? $"节目 {episodes.Count + 1}" : title,
                            hasPage ? episodePage! : page, hasId ? guid : null));
                }
                if (entries.GetArrayLength() < PageSize || episodes.Count == before ||
                    (int.TryParse(Value(data, "total"), out int total) && number * PageSize >= total))
                    break;
            }
            if (episodes.Count > 0)
                break;
        }

        if (episodes.Count == 0 && current is null)
        {
            if (html is null)
                (html, page) = await GetTextAsync(page, Site, token).ConfigureAwait(false);
            foreach (Match anchor in EpisodeAnchor.Matches(html))
            {
                string attributes = anchor.Groups["attributes"].Value;
                string title = Decode(anchor.Groups["text"].Value);
                if (title.Length == 0)
                    title = Decode(Group(AnchorTitle, attributes));
                if (title.Length > 0 && TryHttpUri(page, Decode(Group(Href, attributes)), out Uri? link) &&
                    IsCctvPage(link!) && EpisodeCode.IsMatch(link!.AbsolutePath) && seen.Add(link.AbsolutePath))
                    episodes.Add(new CctvEpisode(title, link));
            }
        }
        if (current is not null && !episodes.Any(episode => episode.VideoId == current.VideoId ||
            episode.Page.AbsolutePath == current.Page.AbsolutePath))
            episodes.Insert(0, current);
        if (episodes.Count == 0)
            throw new InvalidDataException("这张央视专辑没有返回可用的剧集。");
        return episodes;
    }

    private static Dictionary<int, Uri> ReadVariants(string text, Uri playlist)
    {
        var variants = new Dictionary<int, Uri>();
        bool expectUri = false;
        foreach (string line in text.Split('\n'))
        {
            string value = line.Trim();
            if (value.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                expectUri = true;
            else if (expectUri && value.Length > 0 && value[0] != '#')
            {
                if (TryHttpUri(playlist, value, out Uri? url) && GetCode(url!) is int code)
                    variants.TryAdd(code, url!);
                expectUri = false;
            }
        }
        return variants;
    }

    private async Task<(string Text, Uri Address)?> ReadPlaylistAsync(string url, CancellationToken token)
    {
        if (!TryHttpUri(Site, url, out Uri? address))
            return null;
        try
        {
            (string text, Uri finalAddress) = await GetTextAsync(address!, Site, token).ConfigureAwait(false);
            return IsHls(text) ? (text, finalAddress) : null;
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private static bool IsHls(string text) => text.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal);
    private static string ExtractVideoId(string html)
    {
        string guid = Group(PageVideoId, html);
        return guid.Length == 0 ? Group(EmbeddedVideoId, html) : guid;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri address, Uri referer, CancellationToken token)
    {
        (string body, _) = await GetTextAsync(address, referer, token).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private async Task<(string Text, Uri Address)> GetTextAsync(Uri address, Uri referer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        request.Headers.Referrer = referer;
        using HttpResponseMessage response = await (http ?? Http).SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false), response.RequestMessage!.RequestUri!);
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Value(JsonElement element, string name) => Property(element, name).ToString();
    private static string Decode(string value) => WebUtility.HtmlDecode(value).Trim();
    private static string Group(Regex pattern, string value) => pattern.Match(value).Groups[1].Value;
    private static int? GetCode(Uri url)
    {
        string path = url.AbsolutePath;
        string code = Group(VariantCode, path);
        if (code.Length == 0)
            code = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(code, out int number) && CctvQuality.All.Any(level => level.Code == number) ? number : null;
    }
    private static Regex Pattern(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool TryHttpUri(Uri page, string value, out Uri? result)
    {
        result = null;
        return value.Length > 0 && Uri.TryCreate(page, value, out result) && IsHttp(result);
    }

    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
    private static bool IsCctvPage(Uri uri) => IsHttp(uri) && Domains.Any(domain =>
        uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    private static bool IsPlaylist(Uri uri) => Path.GetExtension(uri.AbsolutePath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
}
