using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveRecorder.Core;

/// <summary>表示成品发布失败，并携带原缓存文件的位置。</summary>
/// <param name="recoveryPath">调用方可用于恢复的缓存文件路径。</param>
/// <param name="innerException">发布失败的底层异常。</param>
public sealed class MediaPublishException(string recoveryPath, Exception innerException) :
    IOException($"无法保存媒体文件。已完成的数据保存在 {recoveryPath}。{innerException.Message}", innerException)
{
    public string RecoveryPath { get; } = recoveryPath;
}

// 将已完成的缓存文件移动到输出目录，并处理 Windows 同名后缀。
internal static class MediaOutput
{
    private const int DifferentVolume = 17;

    /// <summary>把完整的缓存文件发布到输出目录。</summary>
    /// <param name="source">已经写完的缓存文件路径。</param>
    /// <param name="directory">输出目录，缺失时会创建。</param>
    /// <param name="name">不含扩展名的成品名称。</param>
    /// <param name="extension">包含前置点号的扩展名。</param>
    /// <param name="useAvailableName">发生同名冲突时是否尝试带编号的文件名。</param>
    /// <returns>最终成品的完整路径。</returns>
    /// <exception cref="MediaPublishException">创建目录、复制或重命名失败。</exception>
    /// <remarks>跨盘时先复制到目标目录中的临时文件，完成后才生成成品名称。</remarks>
    public static string Publish(string source, string directory, string name, string extension, bool useAvailableName = true)
    {
        source = Path.GetFullPath(source);
        string? staging = null;
        try
        {
            directory = Directory.CreateDirectory(directory).FullName;
            for (int count = 1; ; count++)
            {
                string destination = Path.Combine(directory, AvailableName(name, count) + extension);
                if (useAvailableName && Path.Exists(destination))
                    continue;

                int error = Rename(staging ?? source, destination);
                // 跨盘时先在目标目录写入临时文件，复制完成后再给出成品名称。
                if (error == DifferentVolume && staging is null)
                {
                    staging = Path.Combine(directory, $".liverecorder-output-{Guid.NewGuid():N}.tmp");
                    File.Copy(source, staging, overwrite: false);
                    error = Rename(staging, destination);
                }
                if (error == 0)
                {
                    if (staging is not null)
                        TryDelete(source);
                    return destination;
                }
                if (!useAvailableName || error is not (80 or 183) && !Path.Exists(destination))
                    throw new Win32Exception(error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new MediaPublishException(source, ex);
        }
        finally
        {
            if (staging is not null)
                TryDelete(staging);
        }
    }

    internal static string AvailableName(string name, int count) =>
        count == 1 ? name : FormattableString.Invariant($"{name} ({count})");

    private static int Rename(string source, string destination) =>
        MoveFileExW(source, destination, 0) ? 0 : Marshal.GetLastWin32Error();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
    }

    // With no COPY_ALLOWED flag the destination only appears after a same-volume rename.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingPath, string newPath, uint flags);
}
