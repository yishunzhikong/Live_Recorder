using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveRecorder.Core;

public enum LogLevel { Debug = 1, Information = 2, Warning = 3, Error = 4, None = 6 }

/// <summary>定义日志过滤与轮转的设置。</summary>
/// <param name="Level">日志最低级别。设为 None 时关闭日志。</param>
/// <param name="FileSizeMb">每个日志文件的大小上限，单位为十进制 MB。</param>
/// <param name="FileCount">共享日志目录保留的文件数上限。</param>
public sealed record LoggingSettings([property: JsonConverter(typeof(LogLevelConverter))] LogLevel Level = LogLevel.Information,
    int FileSizeMb = 10, int FileCount = 10);

// 兼容旧版的日志级别字符串和数值，同时按当前格式写回。
internal sealed class LogLevelConverter : JsonConverter<LogLevel>
{
    /// <inheritdoc />
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

    /// <inheritdoc />
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
    DeinterlaceMode Deinterlace = DeinterlaceMode.Automatic, int PartDurationSeconds = RecordingOptions.DefaultPartDurationSeconds,
    bool Reencode = false,
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

/// <summary>读取、迁移和更新便携目录中的 settings.json。</summary>
/// <remarks>通过命名互斥锁协调不同实例，并在成功保存后更新内存快照。</remarks>
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

    /// <summary>创建针对指定便携目录的配置存储。</summary>
    /// <param name="directory">包含或将要保存 settings.json 的目录。</param>
    public PortableConfiguration(string directory)
    {
        _directory = directory;
        _path = Path.Combine(directory, "settings.json");
        _mutex = PortableFileMutex.Create(_path);
    }

    /// <summary>本实例最近成功载入或保存的配置快照。</summary>
    public AppSettings Current => Volatile.Read(ref _current);
    /// <summary>本实例最近一次成功恢复时备份的配置路径。</summary>
    /// <value>尚未成功恢复时为 <see langword="null" />。</value>
    public string? RecoveryBackup { get; private set; }

    /// <summary>载入配置，并在缺失或损坏时生成可用配置。</summary>
    /// <param name="toolsDirectory">便携工具目录，用于查找旧版保存的版本偏好。</param>
    /// <remarks>读取不到有效配置时尝试迁移旧偏好。损坏配置在替换时保留备份。</remarks>
    public void Load(string toolsDirectory)
    {
        PortableFileMutex.Enter(_mutex);
        try
        {
            string oldFfmpeg = Path.Combine(toolsDirectory, "ffmpeg", "selected-version.txt");
            string oldYtDlp = Path.Combine(toolsDirectory, "yt-dlp", "selected-version.txt");
            AppSettings? settings = ReadForRecovery(out string? backup);
            if (settings is null)
            {
                settings = new AppSettings { Tools = new ToolSettings(ReadLegacy(oldFfmpeg), ReadLegacy(oldYtDlp)) };
                Save(settings, backup);
            }
            Volatile.Write(ref _current, settings);
            RemoveLegacy(oldFfmpeg);
            RemoveLegacy(oldYtDlp);
        }
        finally { _mutex.ReleaseMutex(); }
    }

    /// <summary>在跨进程锁内读取、修改并按需保存最新配置。</summary>
    /// <param name="update">根据当前配置生成新配置的同步委托。</param>
    /// <remarks>委托在持锁期间运行，应尽快返回。若磁盘配置损坏，使用本实例的配置快照作为修改基础。</remarks>
    public void Update(Func<AppSettings, AppSettings> update)
    {
        PortableFileMutex.Enter(_mutex);
        try
        {
            // 持锁重新读取磁盘状态，保留其他实例已经保存的设置。
            AppSettings settings = ReadForRecovery(out string? backup) ?? Current;
            AppSettings next = Normalize(update(settings));
            if (settings != next || backup is not null || !File.Exists(_path))
                Save(next, backup);
            Volatile.Write(ref _current, next);
        }
        finally { _mutex.ReleaseMutex(); }
    }

    private AppSettings? ReadForRecovery(out string? backup)
    {
        backup = null;
        try { return Read(); }
        catch (JsonException)
        {
            backup = Path.Combine(_directory, FormattableString.Invariant(
                $"settings-broken-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
            return null;
        }
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

    private void Save(AppSettings settings, string? backup)
    {
        Directory.CreateDirectory(_directory);
        string pending = Path.Combine(_directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(file, settings, Json);
            if (backup is null)
                File.Move(pending, _path, overwrite: true);
            else
            {
                // 用新配置原位替换，同时留下损坏原件供排查。
                File.Replace(pending, _path, backup);
                RecoveryBackup = backup;
            }
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
                PartDurationSeconds = media.PartDurationSeconds > 0 ? media.PartDurationSeconds : RecordingOptions.DefaultPartDurationSeconds,
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
