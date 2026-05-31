using Quiver.Storage.Wal;

namespace Quiver.FuzzTests.Targets;

/// <summary>
/// TS-5: WAL セグメントファイル全体の parse 経路を任意バイト列で叩く。
/// <see cref="WriteAheadLog"/> コンストラクタ (RebuildState) と
/// <see cref="WriteAheadLog.OpenReader"/>.TryReadNext を一巡させる。
///
/// 契約:
///   - 任意バイト列を <c>wal.00000000.log</c> として配置しても、コンストラクタ
///     は破損を黙って吸収して return しなければならない (RebuildState の catch 経由)。
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
            string segPath = Path.Combine(dir, "wal.00000000.log");
            File.WriteAllBytes(segPath, input.ToArray());

            using var wal = new WriteAheadLog(dir);
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
