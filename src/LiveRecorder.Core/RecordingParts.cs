using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

internal static partial class RecordingParts
{
    private const int PacketSize = 188;
    private const int BufferSize = PacketSize * 512;

    [GeneratedRegex(@"^part-\d{8}\.ts$", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();

    public static FileInfo[] Find(string directory)
    {
        FileInfo[] files = new DirectoryInfo(directory).EnumerateFiles("part-*.ts")
            .Where(file => PartName().IsMatch(file.Name))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        for (int index = 0; index < files.Length; index++)
        {
            FileInfo file = files[index];
            if (file.Name != $"part-{index + 1:D8}.ts" || file.Length == 0 || file.Length % PacketSize != 0)
                throw new InvalidDataException("录制分片缺失或已损坏。请检查分片编号和文件大小。");
        }
        return files;
    }

    public static async Task SplitAsync(Stream source, string directory, long maximumBytes, IProgress<MediaProgress>? progress)
    {
        long limit = maximumBytes / PacketSize * PacketSize;
        byte[] buffer = new byte[BufferSize];
        int partNumber = 0;
        long total = 0;
        int buffered = 0;
        FileStream? destination = null;
        string? incompletePath = null;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(buffered)).ConfigureAwait(false);
                if (read == 0)
                    break;
                int length = buffered + read;
                int packetBytes = length / PacketSize * PacketSize;
                int offset = 0;
                while (offset < packetBytes)
                {
                    if (destination is null)
                    {
                        incompletePath = Path.Combine(directory, $"part-{partNumber + 1:D8}.ts.part");
                        destination = new FileStream(incompletePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                            BufferSize, FileOptions.Asynchronous);
                    }
                    int count = (int)Math.Min(packetBytes - offset, limit - destination.Position);
                    await destination.WriteAsync(buffer.AsMemory(offset, count)).ConfigureAwait(false);
                    total += count;
                    offset += count;
                    if (destination.Position == limit)
                        await FinishPartAsync().ConfigureAwait(false);
                }
                buffered = length - packetBytes;
                buffer.AsSpan(packetBytes, buffered).CopyTo(buffer);
            }
            if (destination is not null)
                await FinishPartAsync().ConfigureAwait(false);
        }
        finally
        {
            if (destination is not null)
                await destination.DisposeAsync().ConfigureAwait(false);
        }

        async Task FinishPartAsync()
        {
            await destination!.DisposeAsync().ConfigureAwait(false);
            destination = null;
            File.Move(incompletePath!, Path.ChangeExtension(incompletePath!, null));
            partNumber++;
            progress?.Report(new MediaProgress(Bytes: total, Parts: partNumber));
        }
    }
}
