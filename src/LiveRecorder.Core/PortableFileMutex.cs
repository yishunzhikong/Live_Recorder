using System.Security.Cryptography;
using System.Text;

namespace LiveRecorder.Core;

internal static class PortableFileMutex
{
    public static Mutex Create(string path)
    {
        string absolute = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            absolute = absolute.ToUpperInvariant();
        return new Mutex(false, "LiveRecorder-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(absolute))));
    }

    public static void Enter(Mutex mutex)
    {
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* The current thread acquired ownership. */ }
    }
}
