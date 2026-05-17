namespace Quiver.Backend.Tests.Faults;

/// <summary>
/// BA-9 fault injector: flips a single bit at a given offset (default: a few
/// bytes in from the start of the file, where the WAL / page CRC32C lives).
/// Recovery is expected to detect the mismatch and reject the record / page
/// — never accept a corrupted page as valid.
/// </summary>
internal static class ChecksumCorruptor
{
    public static void FlipBitAt(string path, long offset, int bitInByte = 0)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(path);
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (offset >= fs.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        fs.Position = offset;
        int b = fs.ReadByte();
        if (b < 0) throw new InvalidOperationException("read failed");
        byte flipped = (byte)(b ^ (1 << bitInByte));
        fs.Position = offset;
        fs.WriteByte(flipped);
        fs.Flush(flushToDisk: true);
    }
}
