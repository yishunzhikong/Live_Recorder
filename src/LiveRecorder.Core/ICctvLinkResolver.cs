namespace LiveRecorder.Core;

public sealed record CctvEpisode(string Title, Uri Page, string? VideoId = null);

public sealed record CctvQuality(int Code, string Label)
{
    public static IReadOnlyList<CctvQuality> All { get; } = Array.AsReadOnly<CctvQuality>(
        [new(4000, "4K"), new(2000, "超清"), new(1200, "高清"), new(850, "标清"), new(450, "流畅")]);
}

public sealed record CctvMediaLink(Uri Url, CctvQuality? Quality, IReadOnlyList<CctvQuality> ListedQualities);

public interface ICctvLinkResolver
{
    Task<IReadOnlyList<CctvEpisode>> GetEpisodesAsync(Uri page, bool includeAlbum, CancellationToken token);
    Task<CctvMediaLink> ResolveAsync(CctvEpisode episode, CctvQuality quality, CancellationToken token);
}
