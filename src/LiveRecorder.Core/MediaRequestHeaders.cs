using System.Text.Json;

namespace LiveRecorder.Core;

/// <summary>封装从 yt-dlp 解析结果中筛选的媒体请求头。</summary>
/// <remarks>仅允许 User-Agent、Referer 和 Origin，解析结果中的 Authorization 和 Cookie 不在转发范围内。</remarks>
public sealed class MediaRequestHeaders
{
    private MediaRequestHeaders(string? userAgent, string? referer, string? origin) =>
        (UserAgent, Referer, Origin) = (userAgent, referer, origin);

    /// <summary>经筛选的客户端标识。上游未提供时为空。</summary>
    public string? UserAgent { get; }

    /// <summary>经筛选的 HTTP(S) 来源页面地址。</summary>
    public string? Referer { get; }

    /// <summary>经筛选的来源协议、主机和端口。</summary>
    public string? Origin { get; }

    /// <summary>读取选中格式的请求头，缺失项从页面级请求头补齐。</summary>
    /// <param name="format">选中格式的请求头 JSON 对象。</param>
    /// <param name="media">页面级请求头 JSON 对象。</param>
    /// <returns>至少取得一个有效请求头时返回配置，否则返回 <see langword="null" />。</returns>
    internal static MediaRequestHeaders? FromExtractor(JsonElement format, JsonElement media)
    {
        string? userAgent = Read("User-Agent");
        string? referer = Read("Referer");
        string? origin = Read("Origin");
        return userAgent is null && referer is null && origin is null ? null : new(userAgent, referer, origin);

        // 所选格式的请求头优先，缺失时使用页面级的值。
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
            // 控制字符可能破坏外部工具使用的 HTTP 请求头边界。
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

    /// <summary>向当前 HTTP 媒体请求补充已验证的请求头。</summary>
    /// <param name="request">还未发送的媒体请求。</param>
    internal void Apply(HttpRequestMessage request)
    {
        if (UserAgent is not null)
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (Referer is not null)
            request.Headers.TryAddWithoutValidation("Referer", Referer);
        if (Origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", Origin);
    }

    /// <summary>为 FFmpeg 生成调用方可直接追加的 HTTP 请求头参数。</summary>
    /// <returns>有序参数数组。没有可用请求头时返回空数组。</returns>
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
