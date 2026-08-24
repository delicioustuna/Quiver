namespace Yatagarasu.Backend.Tests.Faults;

/// <summary>
/// 指定 offset の 1 ビットを反転する fault injector。
/// 通常は WAL / page の CRC32C があるファイル先頭付近を対象とする。
/// recovery は不一致を検出してレコードまたはページを拒否し、
/// 破損ページを有効なものとして受け入れてはならない。
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
