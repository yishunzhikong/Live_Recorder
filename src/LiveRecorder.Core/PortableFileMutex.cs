using System.Security.Cryptography;
using System.Text;

namespace LiveRecorder.Core;

/// <summary>从便携路径创建跨进程命名互斥锁。</summary>
internal static class PortableFileMutex
{
    /// <summary>根据绝对路径创建或打开命名互斥锁。</summary>
    /// <param name="path">受保护的文件或目录路径。</param>
    /// <returns>尚未获取的互斥锁。调用方负责释放对象。</returns>
    public static Mutex Create(string path)
    {
        string absolute = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            absolute = absolute.ToUpperInvariant();
        // 将路径转换成固定长度名称，避免目录字符进入互斥锁名称。
        return new Mutex(false, "LiveRecorder-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(absolute))));
    }

    /// <summary>等待当前线程取得互斥锁，包括在原持有线程异常退出后接管。</summary>
    /// <param name="mutex">需要获取的互斥锁。</param>
    /// <remarks>调用方须在同一线程释放互斥锁。</remarks>
    public static void Enter(Mutex mutex)
    {
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* The current thread acquired ownership. */ }
    }
}
