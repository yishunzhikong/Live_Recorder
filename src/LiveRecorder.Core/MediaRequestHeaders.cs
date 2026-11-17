using System.Text.Json;

namespace LiveRecorder.Core;

public sealed class MediaRequestHeaders
{
    private MediaRequestHeaders(string? userAgent, string? referer, string? origin) =>
        (UserAgent, Referer, Origin) = (userAgent, referer, origin);

    public string? UserAgent { get; }
    public string? Referer { get; }
    public string? Origin { get; }

    internal static MediaRequestHeaders? FromExtractor(JsonElement format, JsonElement media)
    {
        string? userAgent = Read("User-Agent");
        string? referer = Read("Referer");
        string? origin = Read("Origin");
        return userAgent is null && referer is null && origin is null ? null : new(userAgent, referer, origin);

        string? Read(string name) => ReadHeader(format, name) ?? ReadHeader(media, name);
    }

    private static string? ReadHeader(JsonElement headers, string name)
    {
        if (headers.ValueKind != JsonValueKind.Object)
            return null;
        foreach (JsonProperty property in headers.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
                continue;
            string value = property.Value.GetString()!;
            if (value.Length is 0 or > 4096 || value.Any(char.IsControl))
                continue;
            if (name == "User-Agent")
                return value;
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
                return name == "Origin" ? uri.GetLeftPart(UriPartial.Authority) : uri.GetComponents(
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        }
        return null;
    }

    internal void Apply(HttpRequestMessage request)
    {
        if (UserAgent is not null)
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (Referer is not null)
            request.Headers.TryAddWithoutValidation("Referer", Referer);
        if (Origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", Origin);
    }

    internal string[] FfmpegArguments()
    {
        var arguments = new List<string>();
        if (UserAgent is not null)
            arguments.AddRange(["-user_agent", UserAgent]);
        if (Referer is not null)
            arguments.AddRange(["-referer", Referer]);
        if (Origin is not null)
            arguments.AddRange(["-headers", $"Origin: {Origin}\r\n"]);
        return arguments.ToArray();
    }
}
