using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

/// <summary>定义录制和合并共用的 MPEG-TS 分片规则。</summary>
internal static partial class RecordingParts
{
    private const int PacketSize = 188;
    private const int BufferSize = PacketSize * 512;

    [GeneratedRegex(@"^part-\d{8}\.ts$", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();

    /// <summary>按编号查找录制分片并校验序列完整性。</summary>
    /// <param name="directory">包含录制分片的目录。</param>
    /// <returns>按编号排序的分片数组。目录无匹配分片时返回空数组。</returns>
    /// <exception cref="InvalidDataException">匹配到的序列有缺号、空文件或非完整传输包。</exception>
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

    /// <summary>按完整传输包边界把 MPEG-TS 输出切分成连续编号的文件。</summary>
    /// <param name="source">上游提供的 MPEG-TS 流。</param>
    /// <param name="createDirectory">首次开始写分片时调用，用于创建目标目录。</param>
    /// <param name="cacheDirectory">未发布分片的暂存目录。</param>
    /// <param name="maximumBytes">单段最大字节数，向下对齐到 188 字节的传输包边界。</param>
    /// <param name="progress">可选的进度接收方。字节数包含当前分片，分片数仅统计已保存的文件。</param>
    /// <param name="received">每次成功读取数据后调用，用于重置录制无数据计时。</param>
    /// <returns>读取上游直到流结束并完成分片保存的任务。</returns>
    /// <exception cref="MediaPublishException">完整分片无法发布到目标目录。</exception>
    public static async Task SplitAsync(Stream source, Func<string> createDirectory, string cacheDirectory,
        long maximumBytes, IProgress<MediaProgress>? progress, Action received)
    {
        // 分片大小向下对齐到完整传输包，避免在包中间切断。
        long limit = maximumBytes / PacketSize * PacketSize;
        byte[] buffer = new byte[BufferSize];
        int partNumber = 0;
        long total = 0;
        long lastReportedBytes = 0;
        var progressClock = Stopwatch.StartNew();
        int buffered = 0;
        FileStream? destination = null;
        string? directory = null;
        bool preservePart = false;
        string incompletePath = Path.Combine(cacheDirectory, $"record-{Guid.NewGuid():N}.part");
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(buffered)).ConfigureAwait(false);
                if (read == 0)
                    break;
                received();
                int length = buffered + read;
                int packetBytes = length / PacketSize * PacketSize;
                int offset = 0;
                while (offset < packetBytes)
                {
                    if (destination is null)
                    {
                        directory ??= createDirectory();
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
                // 低码率下填满分片需要较长时间，写入中的字节数也应更新。
                if (progress is not null && total != lastReportedBytes &&
                    (lastReportedBytes == 0 || progressClock.ElapsedMilliseconds >= 200))
                    ReportProgress();
                // 本次未凑齐的包留到下一轮，收齐后再写入。
                buffered = length - packetBytes;
                buffer.AsSpan(packetBytes, buffered).CopyTo(buffer);
            }
            if (destination is not null)
                await FinishPartAsync().ConfigureAwait(false);
        }
        catch (MediaPublishException) { preservePart = true; throw; }
        finally
        {
            if (destination is not null)
                await destination.DisposeAsync().ConfigureAwait(false);
            if (!preservePart)
                File.Delete(incompletePath);
        }

        async Task FinishPartAsync()
        {
            await destination!.DisposeAsync().ConfigureAwait(false);
            destination = null;
            // 编号属于连续序列，遇到占用应报告保存失败，便于保留缓存并排查。
            MediaOutput.Publish(incompletePath, directory!, $"part-{partNumber + 1:D8}", ".ts", useAvailableName: false);
            partNumber++;
            ReportProgress();
        }

        void ReportProgress()
        {
            progress?.Report(new MediaProgress(Bytes: total, Parts: partNumber));
            lastReportedBytes = total;
            progressClock.Restart();
        }
    }
}
