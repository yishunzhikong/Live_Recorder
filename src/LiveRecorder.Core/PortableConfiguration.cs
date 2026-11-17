using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveRecorder.Core;

public enum LogLevel { Debug = 1, Information = 2, Warning = 3, Error = 4, None = 6 }

public sealed record LoggingSettings([property: JsonConverter(typeof(LogLevelConverter))] LogLevel Level = LogLevel.Information,
    int FileSizeMb = 10, int FileCount = 10);

internal sealed class LogLevelConverter : JsonConverter<LogLevel>
{
    public override LogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? name = reader.GetString();
            return name?.ToLowerInvariant() switch
            {
                "trace" => LogLevel.Debug,
                "critical" => LogLevel.Error,
                "info" => LogLevel.Information,
                _ when Enum.TryParse(name, ignoreCase: true, out LogLevel level) => Normalize((int)level),
                _ => LogLevel.Information
            };
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
            return Normalize(value);
        throw new JsonException("日志级别应为字符串或整数。");
    }

    public override void Write(Utf8JsonWriter writer, LogLevel value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));

    private static LogLevel Normalize(int value) => value switch
    {
        0 => LogLevel.Debug,
        5 => LogLevel.Error,
        _ when Enum.IsDefined((LogLevel)value) => (LogLevel)value,
        _ => LogLevel.Information
    };
}

public sealed record MediaSettings(string? OutputDirectory = null, int Mode = 0, VideoEncoder Encoder = VideoEncoder.Software,
    DeinterlaceMode Deinterlace = DeinterlaceMode.Automatic, int PartSizeMb = 1, bool Reencode = false,
    int Volume = 100, bool Muted = false)
{
    public EncodingSettings Encoding { get; init; } = new();
    public RecordingReconnectSettings Reconnect { get; init; } = new();
}

public sealed record ToolSettings(string? Ffmpeg = null, string? YtDlp = null);

public sealed record AppSettings
{
    public LoggingSettings Logging { get; init; } = new();
    public MediaSettings Media { get; init; } = new();
    public ToolSettings Tools { get; init; } = new();
}

public sealed class PortableConfiguration : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly string _directory;
    private readonly string _path;
    private readonly Mutex _mutex;
    private AppSettings _current = new();

    public PortableConfiguration(string directory)
    {
        _directory = directory;
        _path = Path.Combine(directory, "settings.json");
        _mutex = PortableFileMutex.Create(_path);
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public void Load(string toolsDirectory)
    {
        PortableFileMutex.Enter(_mutex);
        try
        {
            string oldFfmpeg = Path.Combine(toolsDirectory, "ffmpeg", "selected-version.txt");
            string oldYtDlp = Path.Combine(toolsDirectory, "yt-dlp", "selected-version.txt");
            AppSettings? settings = Read();
            if (settings is null)
            {
                settings = new AppSettings { Tools = new ToolSettings(ReadLegacy(oldFfmpeg), ReadLegacy(oldYtDlp)) };
                Save(settings);
            }
            Volatile.Write(ref _current, settings);
            RemoveLegacy(oldFfmpeg);
            RemoveLegacy(oldYtDlp);
        }
        finally { _mutex.ReleaseMutex(); }
    }

    public void Update(Func<AppSettings, AppSettings> update)
    {
        PortableFileMutex.Enter(_mutex);
        try
        {
            AppSettings settings = Read() ?? Current;
            AppSettings next = Normalize(update(settings));
            if (settings != next)
                Save(next);
            Volatile.Write(ref _current, next);
        }
        finally { _mutex.ReleaseMutex(); }
    }

    private AppSettings? Read()
    {
        try
        {
            using FileStream file = File.OpenRead(_path);
            using JsonDocument document = JsonDocument.Parse(file);
            AppSettings settings = document.RootElement.Deserialize<AppSettings>(Json)
                ?? throw new JsonException("配置文件必须包含一个 JSON 对象。");
            if (settings.Media is { Encoding: { } encoding } media && !encoding.CopyVideo(media.Deinterlace) &&
                document.RootElement.TryGetProperty("media", out JsonElement mediaJson) && mediaJson.ValueKind == JsonValueKind.Object &&
                mediaJson.TryGetProperty("encoding", out JsonElement encodingJson) && encodingJson.ValueKind == JsonValueKind.Object &&
                !encodingJson.EnumerateObject().Any(property => property.Name.Equals("keepVideoFormat", StringComparison.OrdinalIgnoreCase)))
            {
                settings = settings with { Media = media with { Encoding = encoding with { KeepVideoFormat = false } } };
            }
            return Normalize(settings);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_directory);
        string pending = Path.Combine(_directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(file, settings, Json);
            File.Move(pending, _path, overwrite: true);
        }
        finally { File.Delete(pending); }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        LoggingSettings logging = settings.Logging ?? new();
        MediaSettings media = settings.Media ?? new();
        return settings with
        {
            Logging = logging with
            {
                Level = Enum.IsDefined(logging.Level) ? logging.Level : LogLevel.Information,
                FileSizeMb = Math.Clamp(logging.FileSizeMb, 1, 100),
                FileCount = Math.Clamp(logging.FileCount, 1, 100)
            },
            Media = media with
            {
                Mode = Math.Clamp(media.Mode, 0, 1),
                Encoder = Enum.IsDefined(media.Encoder) ? media.Encoder : VideoEncoder.Software,
                Deinterlace = Enum.IsDefined(media.Deinterlace) ? media.Deinterlace : DeinterlaceMode.Automatic,
                PartSizeMb = Math.Clamp(media.PartSizeMb, 1, 1024),
                Volume = Math.Clamp(media.Volume, 0, 100),
                Encoding = (media.Encoding ?? new()).Normalize(),
                Reconnect = (media.Reconnect ?? new()).Normalize()
            },
            Tools = settings.Tools ?? new()
        };
    }

    private static string? ReadLegacy(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    private static void RemoveLegacy(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void Dispose() => _mutex.Dispose();
}
