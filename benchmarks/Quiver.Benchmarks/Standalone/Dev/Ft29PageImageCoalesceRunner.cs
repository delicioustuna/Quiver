// 起動方法: Program.cs に以下を追加
//   if (args[0] == "--ft29-coalesce") return Quiver.Benchmarks.Standalone.Dev.Ft29PageImageCoalesceRunner.Run();
namespace Quiver.Benchmarks.Standalone.Dev;

/// <summary>
/// Per-tx PageImage coalescing の効果を測る standalone runner。
///
/// transaction-owned write set が PageImage を commit 直前まで遅延し、
/// (fileKind, pageId) ごとに latest-wins 集約する効果を測る。
/// 本ランナーは bulk / per-tx / per-tx-parallel を 1k / 10k entry で測り、
/// transaction shape ごとの bytes/entry を比較する。
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
