using System.Net;
using LiveRecorder.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LiveRecorder;

internal sealed class LivePreview : IAsyncDisposable
{
    private readonly string _directory;
    private readonly WebApplication _server;
    private readonly CancellationTokenSource _conversionToken;
    private Task _conversion = Task.CompletedTask;

    private LivePreview(string directory, WebApplication server, CancellationToken token)
    {
        _directory = directory;
        _server = server;
        _conversionToken = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public Uri Playlist { get; private set; } = null!;
    public Task Completion => _conversion;

    public static async Task<LivePreview> StartAsync(MediaService media, Uri input, CancellationToken token)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var server = builder.Build();
        string directory = Directory.CreateTempSubdirectory("LiveRecorder-").FullName;
        var preview = new LivePreview(directory, server, token);
        string prefix = "/" + Guid.NewGuid().ToString("N");

        try
        {
            server.MapGet(prefix + "/stream.m3u8", () => ReadFile("stream.m3u8", "application/vnd.apple.mpegurl"));
            server.MapGet(prefix + "/{segment}", (string segment) =>
                segment.Length == 19 && segment.StartsWith("segment-", StringComparison.Ordinal) &&
                segment.EndsWith(".ts", StringComparison.Ordinal) && !segment.AsSpan(8, 8).ContainsAnyExceptInRange('0', '9')
                    ? ReadFile(segment, "video/mp2t") : Results.NotFound());
            await server.StartAsync(token);
            preview.Playlist = new Uri(server.Urls.Single() + prefix + "/stream.m3u8");
            preview._conversion = media.CreateLivePreviewAsync(input, directory, preview._conversionToken.Token);

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(25));
            while (!File.Exists(Path.Combine(directory, "stream.m3u8")))
            {
                if (preview._conversion.IsCompleted)
                {
                    await preview._conversion;
                    throw new InvalidDataException("媒体源没有可预览的音视频。");
                }
                await Task.Delay(160, startup.Token);
            }
            return preview;
        }
        catch
        {
            await preview.DisposeAsync();
            throw;
        }

        IResult ReadFile(string name, string contentType)
        {
            try
            {
                var stream = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
                return Results.File(stream, contentType, enableRangeProcessing: true);
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _conversionToken.CancelAsync();
        try { await _conversion; }
        catch (Exception) { /* The caller observes conversion errors through Completion. */ }
        _conversionToken.Dispose();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _server.StopAsync(shutdown.Token); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        await _server.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* A media reader may still be releasing a segment. */ }
    }
}
