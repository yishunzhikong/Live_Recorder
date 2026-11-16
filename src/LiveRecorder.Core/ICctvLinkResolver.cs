namespace LiveRecorder.Core;

public sealed record CctvEpisode(string Title, Uri Page, string? VideoId = null);

public interface ICctvLinkResolver
{
    Task<IReadOnlyList<CctvEpisode>> GetEpisodesAsync(Uri page, bool includeAlbum, CancellationToken token);
    Task<Uri> ResolveAsync(CctvEpisode episode, int? height, CancellationToken token);
}
