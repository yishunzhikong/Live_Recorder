using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

public sealed class CctvLinkResolver : ICctvLinkResolver
{
    private const int PageSize = 100;
    private static readonly Uri Site = new("https://tv.cctv.com/");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string[] Domains = ["cctv.com", "cctv.cn", "cntv.cn", "ipanda.com"];
    private static readonly Regex AlbumCode = Pattern(@"VID[AB][A-Za-z0-9]+");
    private static readonly Regex EpisodeCode = Pattern(@"VIDE[A-Za-z0-9]+");
    private static readonly Regex VideoId = Pattern(@"^[0-9a-f]{32}$");
    private static readonly Regex PageVideoId = Pattern("""\b(?:guid|guid_0|videoCenterId|data-guid|data-pid)\s*["']?\s*[:=]\s*["']([0-9a-f]{32})["']""");
    private static readonly Regex PageTitle = Pattern(@"<title\b[^>]*>(.*?)</title>", RegexOptions.Singleline);
    private static readonly Regex PageAlbumCodes = Pattern("""\bvideotvCodes\s*=\s*["']([^"']+)["']""");
    private static readonly Regex EpisodeAnchor = Pattern("""<a\b(?<attributes>[^>]*)>(?<text>[^<]*)</a\s*>""");
    private static readonly Regex Href = Pattern("""\bhref\s*=\s*["']([^"']+)["']""");
    private static readonly Regex AnchorTitle = Pattern("""\btitle\s*=\s*["']([^"']+)["']""");
    private static readonly Regex Resolution = Pattern(@"(?:[:,])\s*RESOLUTION=\d+x(\d+)");
    private static readonly Regex Bandwidth = Pattern(@"(?:[:,])\s*BANDWIDTH=(\d+)");

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

        string guid = Group(PageVideoId, html);
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

    public async Task<Uri> ResolveAsync(CctvEpisode episode, int? height, CancellationToken token)
    {
        Uri playlist;
        if (IsPlaylist(episode.Page))
        {
            playlist = episode.Page;
        }
        else
        {
            string guid = episode.VideoId ?? "";
            if (!VideoId.IsMatch(guid))
            {
                if (!IsCctvPage(episode.Page))
                    throw new ArgumentException("无效的 CCTV 节目链接。");
                (string html, _) = await GetTextAsync(episode.Page, Site, token).ConfigureAwait(false);
                guid = Group(PageVideoId, html);
            }
            if (!VideoId.IsMatch(guid))
                throw new InvalidDataException("未找到这集节目的 CCTV 视频编号。");

            var endpoint = new Uri($"https://vdn.apps.cntv.cn/api/getHttpVideoInfo.do?pid={guid}");
            using JsonDocument info = await GetJsonAsync(endpoint, Site, token).ConfigureAwait(false);
            JsonElement video = info.RootElement;
            if (Value(video, "is_invalid_copyright") == "1" || Value(video, "is_protected") == "1" || Value(video, "is_preview") == "1")
                throw new InvalidDataException("央视公开接口没有提供这集节目的完整播放权限。");
            if (!TryHttpUri(Site, Value(video, "hls_url"), out Uri? publicPlaylist))
                throw new InvalidDataException("央视接口未返回可用的公开 HLS 播放链接。");
            playlist = publicPlaylist!;
        }

        (string master, Uri finalPlaylist) = await GetTextAsync(playlist, Site, token).ConfigureAwait(false);
        if (!master.TrimStart('\uFEFF').StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new InvalidDataException("服务器未返回有效的 HLS 播放列表。");
        var variants = ReadVariants(master, finalPlaylist);
        if (variants.Count == 0)
        {
            if (height is null && master.Contains("#EXTINF:", StringComparison.Ordinal))
                return finalPlaylist;
            throw new InvalidDataException("播放列表没有可选的分辨率。可尝试选择「自动」。");
        }

        var selected = variants.Where(variant => height is null || variant.Height == height)
            .OrderByDescending(variant => variant.Height).ThenByDescending(variant => variant.Bandwidth)
            .FirstOrDefault();
        if (selected.Url is not null)
            return selected.Url;

        string available = string.Join("、", variants.Select(variant => variant.Height)
            .Where(value => value > 0).Distinct().OrderDescending().Select(value => $"{value}p"));
        throw new InvalidDataException(available.Length == 0
            ? "央视未标注这些播放源的分辨率。请选择「自动」。"
            : $"没有 {height}p 播放源。可选分辨率为 {available}。");
    }

    private static async Task<IReadOnlyList<CctvEpisode>> GetAlbumAsync(string album, Uri page, CancellationToken token,
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

    private static List<(Uri Url, int Height, int Bandwidth)> ReadVariants(string text, Uri playlist)
    {
        var variants = new List<(Uri, int, int)>();
        string? attributes = null;
        foreach (string line in text.Split('\n'))
        {
            string value = line.Trim();
            if (value.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                attributes = value;
            else if (value.Length > 0 && value[0] != '#' && attributes is not null)
            {
                if (TryHttpUri(playlist, value, out Uri? url))
                    variants.Add((url!, Number(Resolution, attributes), Number(Bandwidth, attributes)));
                attributes = null;
            }
        }
        return variants;
    }

    private static async Task<JsonDocument> GetJsonAsync(Uri address, Uri referer, CancellationToken token)
    {
        (string body, _) = await GetTextAsync(address, referer, token).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private static async Task<(string Text, Uri Address)> GetTextAsync(Uri address, Uri referer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        request.Headers.Referrer = referer;
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false), response.RequestMessage!.RequestUri!);
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Value(JsonElement element, string name) => Property(element, name).ToString();
    private static string Decode(string value) => WebUtility.HtmlDecode(value).Trim();
    private static string Group(Regex pattern, string value) => pattern.Match(value).Groups[1].Value;
    private static int Number(Regex pattern, string value) =>
        int.TryParse(Group(pattern, value), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0;
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
