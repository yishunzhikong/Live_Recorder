using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

public enum MediaTool { Ffmpeg, YtDlp }

public sealed record ToolUpdateResult(Version? Before, Version After, bool Updated, bool UsedWebFallback);
public sealed record ToolVersion(string Tag, bool InUse);
public sealed record ToolReleasePage(IReadOnlyList<string> Tags, int? NextPage, bool UsedWebFallback);

public sealed class ToolUpdater
{
    private static readonly HttpClient Network = new(new HttpClientHandler { UseCookies = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Regex VersionText = new(@"\A[0-9]+(?:\.[0-9]+){1,3}\z", RegexOptions.CultureInvariant);
    private static readonly Tool Ffmpeg = new("ffmpeg", "GyanD/codexffmpeg");
    private static readonly Tool YtDlp = new("yt-dlp", "yt-dlp/yt-dlp");
    private const string DenoVersion = "2.9.8";
    private const string DenoArchiveName = "deno-x86_64-pc-windows-msvc.zip";
    private const string DenoSha256 = "cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf";
    private readonly string _directory;
    private readonly string _cacheDirectory;
    private readonly HttpClient _http;
    private readonly PortableConfiguration _configuration;

    private sealed record Tool(string Name, string Repository)
    {
        public string Executable => Name + ".exe";
    }
    private sealed record InstalledTool(string Tag, Version Version, string Path);
    private sealed record ReleaseAsset(string Tag, Version Version, string Name, Uri Address, string Sha256, long? Size);

    public ToolUpdater(string directory, string cacheDirectory, HttpClient? http = null, PortableConfiguration? configuration = null)
    {
        _directory = directory;
        _cacheDirectory = cacheDirectory;
        _http = http ?? Network;
        _configuration = configuration ?? new PortableConfiguration(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(directory))!, "config"));
        if (configuration is null)
            _configuration.Load(directory);
    }

    public string? GetExecutable(MediaTool tool) => FindActive(Definition(tool))?.Path;

    public string? GetActiveTag(MediaTool tool) => FindActive(Definition(tool))?.Tag;

    public IReadOnlyList<ToolVersion> GetInstalledVersions(MediaTool kind)
    {
        Tool tool = Definition(kind);
        InstalledTool[] installed = ReadInstalled(tool);
        string? active = FindActive(tool, installed)?.Tag;
        return installed.Select(version => new ToolVersion(version.Tag, version.Tag == active)).ToArray();
    }

    public async Task SelectVersionAsync(MediaTool kind, string tag, CancellationToken token)
    {
        Tool tool = Definition(kind);
        InstalledTool installed = FindVersion(tool, tag);
        await VerifyVersionAsync(kind, installed.Path, installed.Version, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _configuration.Update(settings => WithPreference(settings, tool, installed.Tag));
    }

    public void DeleteVersion(MediaTool kind, string tag)
    {
        Tool tool = Definition(kind);
        InstalledTool installed = FindVersion(tool, tag);
        string staging = CreateStagingDirectory();
        bool cleanStaging = true;
        try
        {
            string directory = Path.GetDirectoryName(installed.Path)!;
            string removed = Path.Combine(staging, "removed");
            Directory.Move(directory, removed);
            cleanStaging = false;
            try
            {
                _configuration.Update(settings => ReadPreference(settings.Tools, tool) == tag ?
                    WithPreference(settings, tool, null) : settings);
                cleanStaging = true;
            }
            catch
            {
                Directory.Move(removed, directory);
                cleanStaging = true;
                throw;
            }
        }
        finally
        {
            if (cleanStaging)
                DeleteStagingDirectory(staging);
        }
    }

    public void SelectLatest(MediaTool kind) =>
        _configuration.Update(settings => WithPreference(settings, Definition(kind), null));

    public string DenoPath => Path.Combine(_directory, "deno", DenoVersion, "deno.exe");

    public async Task<ToolReleasePage> GetReleasePageAsync(MediaTool kind, int page, bool useWeb,
        CancellationToken token, Action? onWebFallback = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        Tool tool = Definition(kind);
        if (useWeb)
            return await ReadWebReleasePageAsync(tool, page, token).ConfigureAwait(false);

        // The API and website use different page sizes, so start from the first web page on fallback.
        var (releases, _) = await ReadWithWebFallbackAsync(
            ct => ReadApiReleasePageAsync(tool, kind, page, ct),
            ct => ReadWebReleasePageAsync(tool, 1, ct), token, onWebFallback).ConfigureAwait(false);
        return releases;
    }

    private async Task<ToolReleasePage> ReadApiReleasePageAsync(Tool tool, MediaTool kind, int page, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = CreateApiRequest(tool, $"releases?per_page=30&page={page}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        EnsureApiSuccess(response);
        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
        var tags = new List<string>();
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!release.GetProperty("draft").GetBoolean() && !release.GetProperty("prerelease").GetBoolean() &&
                VersionText.IsMatch(tag) && Version.TryParse(tag, out Version? version) &&
                FindApiAsset(release, tool, kind, tag, version) is not null)
                tags.Add(tag);
        }
        bool hasNext = response.Headers.TryGetValues("Link", out var links) &&
            links.Any(link => link.Contains("rel=\"next\"", StringComparison.Ordinal));
        return new ToolReleasePage(tags, hasNext ? page + 1 : null, false);
    }

    private static async Task<(T Value, bool UsedWebFallback)> ReadWithWebFallbackAsync<T>(
        Func<CancellationToken, Task<T>> readApi, Func<CancellationToken, Task<T>> readWeb,
        CancellationToken token, Action? onWebFallback)
    {
        try
        {
            return (await readApi(token).ConfigureAwait(false), false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            token.ThrowIfCancellationRequested();
            onWebFallback?.Invoke();
            return (await readWeb(token).ConfigureAwait(false), true);
        }
    }

    private async Task<ToolReleasePage> ReadWebReleasePageAsync(Tool tool, int page, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            // A page can contain only nightly builds. Limit the number of pages read per request.
            const int maxPages = 4;
            for (int count = 1; ; count++)
            {
                ToolReleasePage result = await ReadWebPageAsync(tool, page, timeout.Token).ConfigureAwait(false);
                if (result.Tags.Count > 0 || result.NextPage is not int next || count == maxPages)
                    return result;
                page = next;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException ||
            ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            string detail = ex is OperationCanceledException ? "连接超时。" : ex.Message;
            throw new HttpRequestException($"GitHub API 查询失败（HTTP 403），官方发布页版本列表读取失败。{detail}", ex);
        }
    }

    private async Task<ToolReleasePage> ReadWebPageAsync(Tool tool, int page, CancellationToken token)
    {
        string path = $"/{tool.Repository}/releases";
        var address = new Uri($"https://github.com{path}?page={page}");
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != address)
            throw new InvalidDataException("GitHub 返回的发行版列表地址不匹配。");
        string html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        MatchCollection releases = Regex.Matches(html, """<section\b[^>]*\sdata-release-anchor="release-([^"]+)"[^>]*>""");
        if (releases.Count == 0 && page == 1)
            throw new InvalidDataException("无法从 GitHub 官方发布页读取版本列表。");
        string[] tags = releases.Select(match => match.Groups[1].Value)
            .Where(tag => VersionText.IsMatch(tag) && Version.TryParse(tag, out _))
            .Distinct(StringComparer.Ordinal).ToArray();
        bool hasNext = html.Contains($"href=\"{path}?page={page + 1}\"", StringComparison.Ordinal) &&
            html.Contains("class=\"next_page\"", StringComparison.Ordinal);
        return new ToolReleasePage(tags, hasNext ? page + 1 : null, true);
    }

    public Task<ToolUpdateResult> UpdateAsync(MediaTool kind, IProgress<MediaProgress>? progress,
        CancellationToken token, Action? onWebFallback = null) =>
        InstallAsync(kind, null, progress, token, onWebFallback);

    public Task<ToolUpdateResult> InstallVersionAsync(MediaTool kind, string tag, IProgress<MediaProgress>? progress,
        CancellationToken token, Action? onWebFallback = null)
    {
        tag = tag.Trim();
        if (!VersionText.IsMatch(tag) || !Version.TryParse(tag, out _))
            throw new ArgumentException("请输入正式发布的版本号，例如 9.0.2 或 2026.10.26。");
        return InstallAsync(kind, tag, progress, token, onWebFallback);
    }

    private async Task<ToolUpdateResult> InstallAsync(MediaTool kind, string? requestedTag, IProgress<MediaProgress>? progress,
        CancellationToken token, Action? onWebFallback)
    {
        Tool tool = Definition(kind);
        InstalledTool[] installed = ReadInstalled(tool);
        InstalledTool? current = requestedTag is null ? installed.FirstOrDefault() :
            installed.FirstOrDefault(version => version.Tag == requestedTag);
        if (current is not null)
            await VerifyVersionAsync(kind, current.Path, current.Version, token).ConfigureAwait(false);
        Version? before = current?.Version;
        ReleaseAsset? release = null;
        bool usedWebFallback = false;
        if (requestedTag is null || current is null)
        {
            (release, usedWebFallback) = await ReadWithWebFallbackAsync(
                ct => ReadApiReleaseAsync(tool, kind, before, requestedTag, ct),
                ct => ReadWebReleaseAsync(tool, kind, before, requestedTag, ct), token, onWebFallback).ConfigureAwait(false);
        }
        if (kind == MediaTool.YtDlp)
            await InstallDenoAsync(progress, token).ConfigureAwait(false);
        if (release is null)
            return new ToolUpdateResult(before, current!.Version, false, usedWebFallback);

        string toolDirectory = Directory.CreateDirectory(Path.Combine(_directory, tool.Name)).FullName;
        string staging = CreateStagingDirectory();
        try
        {
            string artifact = Path.Combine(staging, release.Name);
            await DownloadAsync(release.Address, release.Size, release.Sha256, artifact, progress, token).ConfigureAwait(false);
            string executable = Path.Combine(staging, tool.Executable);
            if (kind == MediaTool.Ffmpeg)
            {
                using (ZipArchive zip = ZipFile.OpenRead(artifact))
                {
                    string root = Path.GetFileNameWithoutExtension(release.Name);
                    foreach (string member in new[] { "bin/ffmpeg.exe", "LICENSE", "README.txt" })
                    {
                        ZipArchiveEntry entry = zip.GetEntry($"{root}/{member}")
                            ?? throw new InvalidDataException($"FFmpeg 压缩包缺少 {member}。");
                        await using Stream source = entry.Open();
                        await using FileStream output = File.Create(Path.Combine(staging, Path.GetFileName(member)));
                        await source.CopyToAsync(output, token).ConfigureAwait(false);
                    }
                }
                File.Delete(artifact);
            }
            await VerifyVersionAsync(kind, executable, release.Version, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(toolDirectory, release.Tag);
            try { Directory.Move(staging, target); }
            catch (IOException) when (Directory.Exists(target))
            {
                await VerifyVersionAsync(kind, Path.Combine(target, tool.Executable), release.Version, token).ConfigureAwait(false);
            }
            return new ToolUpdateResult(before, release.Version, true, usedWebFallback);
        }
        finally
        {
            DeleteStagingDirectory(staging);
        }
    }

    private async Task InstallDenoAsync(IProgress<MediaProgress>? progress, CancellationToken token)
    {
        string executable = DenoPath;
        if (File.Exists(executable))
            return;
        string target = Path.GetDirectoryName(executable)!;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string staging = CreateStagingDirectory();
        try
        {
            string artifact = Path.Combine(staging, DenoArchiveName);
            var address = new Uri($"https://github.com/denoland/deno/releases/download/v{DenoVersion}/{DenoArchiveName}");
            await DownloadAsync(address, null, DenoSha256, artifact, progress, token).ConfigureAwait(false);
            using (ZipArchive zip = ZipFile.OpenRead(artifact))
            {
                ZipArchiveEntry entry = zip.GetEntry("deno.exe") ?? throw new InvalidDataException("Deno 压缩包缺少 deno.exe。");
                await using Stream source = entry.Open();
                await using FileStream output = File.Create(Path.Combine(staging, "deno.exe"));
                await source.CopyToAsync(output, token).ConfigureAwait(false);
            }
            File.Delete(artifact);
            token.ThrowIfCancellationRequested();
            try { Directory.Move(staging, target); }
            catch (IOException) when (File.Exists(executable)) { /* Another instance installed the same release. */ }
        }
        finally { DeleteStagingDirectory(staging); }
    }

    private string CreateStagingDirectory() =>
        Directory.CreateDirectory(Path.Combine(_cacheDirectory, $"update-{Guid.NewGuid():N}")).FullName;

    private static void DeleteStagingDirectory(string staging)
    {
        if (!Directory.Exists(staging))
            return;
        try { Directory.Delete(staging, recursive: true); }
        catch (IOException ex) { Debug.WriteLine(ex); }
        catch (UnauthorizedAccessException ex) { Debug.WriteLine(ex); }
    }

    private async Task<ReleaseAsset?> ReadApiReleaseAsync(Tool tool, MediaTool kind, Version? before, string? requestedTag,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string endpoint = requestedTag is null ? "latest" : $"tags/{requestedTag}";
        using var request = CreateApiRequest(tool, $"releases/{endpoint}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && requestedTag is not null)
            throw new InvalidDataException($"GitHub 上找不到 {tool.Name} {requestedTag} 的正式发行版。");
        EnsureApiSuccess(response);
        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
        JsonElement release = document.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (requestedTag is not null && requestedTag != tag)
            throw new InvalidDataException("上游返回的版本与所选版本不匹配。");
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("上游未返回有效的稳定版。");
        Version latest = ParseReleaseTag(tag);
        if (before is not null && latest <= before)
            return null;

        return FindApiAsset(release, tool, kind, tag, latest) ??
            throw new InvalidDataException("上游未提供该版本的下载文件及 SHA-256 校验值。");
    }

    private static HttpRequestMessage CreateApiRequest(Tool tool, string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{tool.Repository}/{endpoint}");
        request.Headers.UserAgent.ParseAdd("LiveRecorder");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } accessToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static void EnsureApiSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        bool rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.StatusCode == HttpStatusCode.Forbidden && (response.Headers.RetryAfter is not null ||
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"));
        string status = $"HTTP {(int)response.StatusCode}";
        string message = rateLimited
            ? $"GitHub API 限流（{status}），请稍后重试。"
            : $"GitHub API 查询失败（{status}）。";
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static ReleaseAsset? FindApiAsset(JsonElement release, Tool tool, MediaTool kind, string tag, Version version)
    {
        string name = AssetName(tool, kind, tag);
        JsonElement asset = release.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(entry => entry.GetProperty("name").GetString() == name &&
                entry.GetProperty("state").GetString() == "uploaded");
        if (asset.ValueKind == JsonValueKind.Undefined || !asset.TryGetProperty("digest", out JsonElement digest) ||
            digest.GetString() is not { } publishedHash || !Regex.IsMatch(publishedHash, @"\Asha256:[0-9a-fA-F]{64}\z") ||
            !asset.GetProperty("size").TryGetInt64(out long size) || size <= 0)
            return null;
        Uri address = AssetAddress(tool, tag, name);
        if (asset.GetProperty("browser_download_url").GetString() != address.AbsoluteUri)
            throw new InvalidDataException("上游返回的下载地址与版本不匹配。");
        return new ReleaseAsset(tag, version, name, address, publishedHash[7..], size);
    }

    private async Task<ReleaseAsset?> ReadWebReleaseAsync(Tool tool, MediaTool kind, Version? before, string? requestedTag,
        CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            string releases = $"/{tool.Repository}/releases";
            string tagPrefix = releases + "/tag/";
            string releasePath = requestedTag is null ? releases + "/latest" : tagPrefix + requestedTag;
            using var response = await _http.GetAsync("https://github.com" + releasePath,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Uri? page = response.RequestMessage?.RequestUri;
            if (page is null || page.Scheme != Uri.UriSchemeHttps || page.Host != "github.com" ||
                !page.IsDefaultPort || page.UserInfo.Length != 0 ||
                !page.AbsolutePath.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub 发布页未跳转到该项目的稳定版本。");
            string tag = page.AbsolutePath[tagPrefix.Length..];
            if (requestedTag is not null && requestedTag != tag)
                throw new InvalidDataException("GitHub 发布页返回的版本与所选版本不匹配。");
            Version latest = ParseReleaseTag(tag);
            if (before is not null && latest <= before)
                return null;

            string name = AssetName(tool, kind, tag);
            Uri address = AssetAddress(tool, tag, name);
            var assetsPage = new Uri($"https://github.com{releases}/expanded_assets/{tag}");
            using var assets = await _http.GetAsync(assetsPage, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            assets.EnsureSuccessStatusCode();
            if (assets.RequestMessage?.RequestUri != assetsPage)
                throw new InvalidDataException("GitHub 下载列表地址与版本不匹配。");
            string html = await assets.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            // The asset's copy button identifies its published digest by filename.
            string label = Regex.Escape($"Copy to clipboard digest for {name}");
            MatchCollection digests = Regex.Matches(html,
                $$"""<clipboard-copy\s+[^>]*\saria-label="{{label}}"[^>]*\svalue="sha256:([0-9a-fA-F]{64})"[^>]*>""");
            if (digests.Count != 1 || !html.Contains($"href=\"{address.AbsolutePath}\"", StringComparison.Ordinal))
                throw new InvalidDataException($"GitHub 发布页未提供 {name} 及其 SHA-256 校验值。");
            return new ReleaseAsset(tag, latest, name, address, digests[0].Groups[1].Value, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException ||
            ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            string detail = ex is OperationCanceledException ? "连接超时。" : ex.Message;
            throw new HttpRequestException($"GitHub API 查询失败（HTTP 403），改用官方发布页查询时出错。{detail}", ex);
        }
    }

    private static Version ParseReleaseTag(string tag) =>
        VersionText.IsMatch(tag) && Version.TryParse(tag, out Version? version)
            ? version : throw new InvalidDataException("上游未返回有效的稳定版。");

    private static string AssetName(Tool tool, MediaTool kind, string tag) =>
        kind == MediaTool.Ffmpeg ? $"ffmpeg-{tag}-essentials_build.zip" : tool.Executable;

    private static Uri AssetAddress(Tool tool, string tag, string name) =>
        new($"https://github.com/{tool.Repository}/releases/download/{tag}/{name}");

    private async Task DownloadAsync(Uri address, long? publishedSize, string sha256, string file,
        IProgress<MediaProgress>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long size = publishedSize ?? response.Content.Headers.ContentLength ?? 0;
        if (size <= 0)
            throw new InvalidDataException("GitHub 下载响应未提供文件大小。");
        await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long received = 0;
        var reportTimer = Stopwatch.StartNew();
        while (true)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            int read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            if (read == 0)
                break;
            received += read;
            if (received > size)
                throw new InvalidDataException("下载文件大小超过上游记录。");
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            if (progress is not null && reportTimer.ElapsedMilliseconds >= 200)
            {
                progress.Report(new MediaProgress(Bytes: received, TotalBytes: size));
                reportTimer.Restart();
            }
        }
        if (received != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载文件的大小或 SHA-256 与上游记录不符。");
    }

    private async Task VerifyVersionAsync(MediaTool kind, string executable, Version expected, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        ProcessStartInfo start = ToolProcess.CreateStartInfo(executable, _cacheDirectory);
        start.Arguments = kind == MediaTool.Ffmpeg ? "-version" : "--ignore-config --version";
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string stdout = await output.ConfigureAwait(false);
            string pattern = kind == MediaTool.Ffmpeg ? @"\Affmpeg version ([0-9.]+)" : @"\A([0-9.]+)(?:\s|\z)";
            Match actual = Regex.Match(stdout, pattern, RegexOptions.CultureInvariant);
            if (process.ExitCode != 0 || !actual.Success || !Version.TryParse(actual.Groups[1].Value, out Version? version) ||
                version != expected)
                throw new InvalidDataException($"{Path.GetFileName(executable)} 报告的版本与预期 {expected} 不符。");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    private static Tool Definition(MediaTool kind) => kind == MediaTool.Ffmpeg ? Ffmpeg : YtDlp;

    private InstalledTool? FindActive(Tool tool, InstalledTool[]? versions = null)
    {
        versions ??= ReadInstalled(tool);
        string? preferred = ReadPreference(_configuration.Current.Tools, tool);
        return versions.FirstOrDefault(version => version.Tag == preferred) ?? versions.FirstOrDefault();
    }

    private InstalledTool FindVersion(Tool tool, string tag) =>
        ReadInstalled(tool).FirstOrDefault(version => version.Tag == tag)
        ?? throw new InvalidOperationException($"{tool.Name} {tag} 未安装。");

    private static string? ReadPreference(ToolSettings preferences, Tool tool) =>
        tool == Ffmpeg ? preferences.Ffmpeg : preferences.YtDlp;

    private static AppSettings WithPreference(AppSettings settings, Tool tool, string? tag) =>
        settings with { Tools = tool == Ffmpeg ? settings.Tools with { Ffmpeg = tag } : settings.Tools with { YtDlp = tag } };

    private InstalledTool[] ReadInstalled(Tool tool)
    {
        var installed = new List<InstalledTool>();
        string root = Path.Combine(_directory, tool.Name);
        if (!Directory.Exists(root))
            return [];
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            string path = Path.Combine(directory, tool.Executable);
            if (VersionText.IsMatch(name) && Version.TryParse(name, out Version? version) && File.Exists(path))
                installed.Add(new InstalledTool(name, version, path));
        }
        return installed.OrderByDescending(version => version.Version).ThenBy(version => version.Tag, StringComparer.Ordinal).ToArray();
    }
}
