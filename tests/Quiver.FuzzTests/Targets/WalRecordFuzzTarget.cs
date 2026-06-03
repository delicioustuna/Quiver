using Quiver.Storage.Wal;

namespace Quiver.FuzzTests.Targets;

/// <summary>
/// TS-5 / ARCH-4 増分7: 単一ファイル WAL の parse 経路を任意バイト列で叩く。
/// <see cref="WriteAheadLog"/> コンストラクタ (RebuildState) と
/// <see cref="WriteAheadLog.OpenReader"/>.TryReadNext を一巡させる。
///
/// 契約:
///   - 任意バイト列を WAL サイドカーファイルとして配置しても、コンストラクタは破損を
///     黙って吸収して return しなければならない (RebuildState の catch 経由)。
///   - reader.TryReadNext は <c>false</c> を返して終了するだけで、例外を投げてはならない。
///   - IOException / EndOfStreamException 系は temp file 取り回しの環境要因として
///     許容するが、CorruptionException / NullReferenceException / IndexOutOfRangeException
///     等は fuzz 発見対象としてバブルアップさせる。
/// </summary>
public static class WalRecordFuzzTarget
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "quiver-fuzz-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string walPath = Path.Combine(dir, "wal");
            File.WriteAllBytes(walPath, input.ToArray());

            using var wal = new WriteAheadLog(walPath);
            using var reader = wal.OpenReader(0);
            int safety = 0;
            while (reader.TryReadNext(out _))
            {
                if (++safety > 1_000_000) break; // 無限ループ防御
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
