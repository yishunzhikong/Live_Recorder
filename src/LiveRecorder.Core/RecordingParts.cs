using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveRecorder.Core;

/// <summary>定义录制和合并共用的 MPEG-TS 与 Matroska 分片规则。</summary>
internal static partial class RecordingParts
{
    private const int PacketSize = 188;
    internal const string TransportExtension = ".ts";
    internal const string MatroskaExtension = ".mkv";

    [GeneratedRegex(@"^part-[0-9]{8}\.(?:ts|mkv)$", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();

    /// <summary>按编号查找录制分片并校验序列完整性。</summary>
    /// <param name="directory">包含录制分片的目录。</param>
    /// <returns>按编号排序的分片数组。目录无匹配分片时返回空数组。</returns>
    /// <exception cref="InvalidDataException">序列有缺号、混合封装、空文件或非完整传输包。</exception>
    public static FileInfo[] Find(string directory)
    {
        FileInfo[] files = new DirectoryInfo(directory).EnumerateFiles("part-*")
            .Where(file => PartName().IsMatch(file.Name))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        for (int index = 0; index < files.Length; index++)
        {
            FileInfo file = files[index];
            if (file.Name != PartFileName(index + 1, files[0].Extension) || !IsValidLength(file.Length, file.Extension))
                throw new InvalidDataException("录制分片缺失或已损坏。请检查分片编号和文件大小。");
        }
        return files;
    }

    /// <summary>生成 FFmpeg concat demuxer 使用的 Matroska 分片清单。</summary>
    /// <param name="parts">同一录制目录下已经排序的分片。</param>
    /// <returns>无 BOM 的 UTF-8 清单。</returns>
    public static byte[] MatroskaConcatList(IEnumerable<FileInfo> parts)
    {
        var list = new StringBuilder("ffconcat version 1.0\n");
        foreach (FileInfo part in parts)
        {
            string path = part.FullName.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);
            list.Append("file 'file:").Append(path).Append("'\n");
        }
        return Encoding.UTF8.GetBytes(list.ToString());
    }

    private static string PartFileName(int number, string extension) =>
        FormattableString.Invariant($"part-{number:D8}{extension}");

    private static bool IsValidLength(long length, string extension) =>
        length > 0 && (extension != TransportExtension || length % PacketSize == 0);

    /// <summary>监视 FFmpeg 原生分片的写入进度，并发布已经关闭的文件。</summary>
    /// <param name="recording">负责创建并写入分片的 FFmpeg 任务。</param>
    /// <param name="stagingDirectory">FFmpeg 写入分片的暂存目录。</param>
    /// <param name="extension">当前录制分片的扩展名。</param>
    /// <param name="createDirectory">首次发现有效写入时调用，用于创建录制目录。</param>
    /// <param name="stop">保存失败时用于停止 FFmpeg 的取消源。</param>
    /// <param name="progress">可选的进度接收方。字节数含当前分片，片数仅统计已发布的文件。</param>
    /// <param name="received">发现新媒体字节时调用，用于重置断流计时。</param>
    /// <returns>等待 FFmpeg 结束并发布最后一个分片的任务。</returns>
    public static async Task PublishAsync(Task recording, string stagingDirectory, string extension, Func<string> createDirectory,
        CancellationTokenSource stop, IProgress<MediaProgress>? progress, Action received)
    {
        string? directory = null;
        int published = 0;
        long publishedBytes = 0;
        long previousBytes = 0;
        try
        {
            while (!recording.IsCompleted)
            {
                PublishReady(finished: false);
                await Task.WhenAny(recording, Task.Delay(200)).ConfigureAwait(false);
            }
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { await recording.ConfigureAwait(false); }
            catch (Exception) { /* 保留保存失败的原始异常。 */ }
            throw;
        }

        try { await recording.ConfigureAwait(false); }
        catch (FfmpegFailureException)
        {
            PublishReady(finished: true);
            throw;
        }
        PublishReady(finished: true);

        FileInfo Part(int number) => new(Path.Combine(stagingDirectory, PartFileName(number, extension)));

        void PublishReady(bool finished)
        {
            int previousCount = published;
            long observedBytes = previousBytes;
            FileInfo part = Part(published + 1);
            // segment 封装器关闭当前文件后才会创建下一个文件。末片在进程退出后处理。
            while (part.Exists && (finished || Part(published + 2).Exists))
            {
                part.Refresh();
                long length = part.Length;
                if (length == 0 && finished && !Part(published + 2).Exists)
                {
                    part.Delete();
                    break;
                }
                if (!IsValidLength(length, extension))
                    throw new InvalidDataException($"FFmpeg 分片不完整，已保留缓存文件 {part.FullName}。");
                ObserveBytes(publishedBytes + length);
                MediaOutput.Publish(part.FullName, directory!, Path.GetFileNameWithoutExtension(part.Name), extension,
                    useAvailableName: false);
                publishedBytes += length;
                published++;
                part = Part(published + 1);
            }

            long bytes = publishedBytes + (!finished && part.Exists ? part.Length : 0);
            ObserveBytes(bytes);
            if (bytes != previousBytes || published != previousCount)
                progress?.Report(new MediaProgress(Bytes: bytes, Parts: published));
            previousBytes = bytes;

            void ObserveBytes(long total)
            {
                if (total <= observedBytes)
                    return;
                observedBytes = total;
                received();
                directory ??= createDirectory();
            }
        }
    }
}
