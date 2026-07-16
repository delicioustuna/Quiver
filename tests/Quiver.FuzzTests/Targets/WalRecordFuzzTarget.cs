using Quiver.Storage.Wal;

namespace Quiver.FuzzTests.Targets;

/// <summary>
/// 単一ファイル WAL の parse 経路を任意バイト列で叩く。
/// <see cref="WriteAheadLog"/> コンストラクタ (RebuildState) と
/// <see cref="WriteAheadLog.OpenReader"/>.TryReadNext を一巡させる。
///
/// 任意入力は正常に parse されるか、strict parser の定義済み format/corruption 例外で拒否される。
/// NullReferenceException や IndexOutOfRangeException などの実装例外は fuzz 発見対象として伝播させる。
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
