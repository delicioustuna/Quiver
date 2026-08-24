namespace Yatagarasu.Backend.Tests.Faults;

/// <summary>
/// ファイル末尾の torn write を模擬する fault injector。
/// クラッシュ時に OS が直近の書き込みの一部しか flush しなかった状態を、
/// 対象ファイル末尾 <c>tailBytes</c> のゼロ埋めまたは切り詰めでモデル化する。
///
/// binary backend の WAL segment に対して使用する。
/// recovery は torn record をスキップするか
/// <see cref="Yatagarasu.Core.CorruptionException"/> 系のエラーを明示する。
/// 暗黙の破損は契約違反となる。
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
