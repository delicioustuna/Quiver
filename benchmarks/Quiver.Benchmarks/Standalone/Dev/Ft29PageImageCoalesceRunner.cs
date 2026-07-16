// 起動方法: Program.cs に以下を追加
//   if (args[0] == "--ft29-coalesce") return Quiver.Benchmarks.Standalone.Dev.Ft29PageImageCoalesceRunner.Run();
namespace Quiver.Benchmarks.Standalone.Dev;

/// <summary>
/// Per-tx PageImage coalescing の効果を測る standalone runner。
///
/// 基準は  の per-tx 1k 65,964 B/entry (sequential)。 は WAL レベルで
/// PageImage を「commit 直前まで遅延 + (fileKind, pageId) latest-wins 集約」する
/// 設計のため、効果が出るのは「複数 tx の FlushPending と Append(Commit) が時間的に
/// 重なる」並列 workload。本ランナーは bulk / per-tx (sequential 互換) / per-tx-parallel
/// を 1k / 10k entry で測り、parallel 経路の bytes/entry を sequential 経路と対比する。
///
/// 起動方法: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --ft29-coalesce</c>
/// </summary>
public static class Ft29PageImageCoalesceRunner
{
    public static int Run()
    {
        Console.WriteLine("=== Per-tx PageImage Coalescing ===");
        Console.WriteLine("scenario, entryCount, walBytes, walBytesPerEntry, wallMs");

        int[] sizes = { 1_000, 10_000 };
        string[] scenarios = { "bulk", "per-tx", "per-tx-parallel" };

        foreach (var n in sizes)
        {
            foreach (var s in scenarios)
            {
                var r = IndexWalAmplificationStandalone.Run(s, n);
                Console.WriteLine(
                    $"{r.Scenario},{r.EntryCount},{r.WalBytes},{r.WalBytes / (double)r.EntryCount:F2},{r.WallMs:F1}");
            }
        }
        return 0;
    }
}
