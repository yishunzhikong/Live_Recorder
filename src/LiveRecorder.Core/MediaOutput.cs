using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveRecorder.Core;

public sealed class MediaPublishException(string recoveryPath, Exception innerException) :
    IOException($"无法保存媒体文件。已完成的数据保存在 {recoveryPath}。{innerException.Message}", innerException)
{
    public string RecoveryPath { get; } = recoveryPath;
}

internal static class MediaOutput
{
    private const int DifferentVolume = 17;

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
