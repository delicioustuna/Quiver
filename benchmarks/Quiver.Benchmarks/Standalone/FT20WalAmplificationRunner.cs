namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// FT-20: BenchmarkDotNet を経由せず短時間で WAL 増幅の参考値を取るランナー。
/// 通常 BDN は数十分かかるので、ローカル開発 / commit 時の簡易計測に使う。
///
/// 起動方法: <c>dotnet run --project benchmarks/Quiver.Benchmarks -- --ft20-wal</c>
/// (引数経由で <see cref="Program"/> から呼び出す)。
/// </summary>
public static class FT20WalAmplificationRunner
{
    public static int Run()
    {
        Console.WriteLine("=== FT-20: Index WAL Amplification ===");
        Console.WriteLine("scenario, entryCount, walBytes, walBytesPerEntry, wallMs");

        int[] sizes = { 1_000, 10_000, 100_000 };
        string[] scenarios = { "bulk", "per-tx" };

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
