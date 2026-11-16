namespace LiveRecorder.Core;

public interface ICctvLinkResolver
{
    Task<Uri> ResolveAsync(Uri page, CancellationToken cancellationToken);
}
