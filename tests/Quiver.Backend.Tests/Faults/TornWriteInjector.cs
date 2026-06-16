namespace Quiver.Backend.Tests.Faults;

/// <summary>
/// BA-9 fault injector: simulates a torn write at the tail of a file. After a
/// (simulated) crash the OS may have flushed only part of the most recent
/// write; we model that by zero-filling or truncating the final
/// <c>tailBytes</c> bytes of a target file.
///
/// Used against WAL segments (binary backend).
/// Recovery is expected to either skip the torn record or surface a
/// <see cref="Quiver.Core.CorruptionException"/>-shaped error; silent
/// corruption is a contract violation.
/// </summary>
internal static class TornWriteInjector
{
    public static void ZeroFillTail(string path, int tailBytes)
    {
        if (!File.Exists(path)) return;
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long len = fs.Length;
        long start = Math.Max(0, len - tailBytes);
        fs.Position = start;
        Span<byte> zeros = stackalloc byte[Math.Min(4096, (int)(len - start))];
        long remaining = len - start;
        while (remaining > 0)
        {
            int n = (int)Math.Min(zeros.Length, remaining);
            fs.Write(zeros[..n]);
            remaining -= n;
        }
        fs.Flush(flushToDisk: true);
    }

    public static void TruncateTail(string path, int tailBytes)
    {
        if (!File.Exists(path)) return;
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long newLen = Math.Max(0, fs.Length - tailBytes);
        fs.SetLength(newLen);
        fs.Flush(flushToDisk: true);
    }
}
