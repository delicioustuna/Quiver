using System.Diagnostics;
using Quiver;
using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// FT-26: MVCC 有効時の single-tx 書き込みスループット絶対値を計測するランナー。
///
/// <para>
/// FT-26 完了条件「単一 tx workload で書き込みスループットが MVCC なし比 80% 以上」を
/// 検証するため、本ランナーで現行 (MVCC 有) の絶対値を取り、別 worktree で pre-FT-26
/// commit (754c2bd 等) を build → 同じランナーを移植して同 workload を回し、ratio を求める。
/// </para>
///
/// <para>
/// FT-26 受け入れ時の実測値 (2026-05-25、Windows 11、best-of-3、N=10000):
/// <list type="bullet">
///   <item>CreateNodeOnly          : pre 370k/s → FT-26 345k/s = <b>93%</b></item>
///   <item>CreateNodeWithProperty  : pre 128k/s → FT-26 115k/s = <b>90%</b></item>
///   <item>CreateRelationship      : pre 116k/s → FT-26 106k/s = <b>91%</b></item>
/// </list>
/// いずれも DoD 「80% 以上」を満たす。
/// </para>
///
/// <para>
/// 計測する workload (いずれも single-thread / single tx):
/// <list type="bullet">
///   <item>CreateNodeOnly: <c>N</c> ノード create + commit</item>
///   <item>CreateNodeWithProperty: <c>N</c> ノード create + property set + commit</item>
///   <item>CreateRelationship: <c>N</c> rel create (固定 2 ノード間) + commit</item>
/// </list>
/// 各 workload は 3 回繰り返し、最良値を採用する (JIT warmup と GC 揺らぎを排除)。
/// </para>
///
/// 起動方法: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --ft26-mvcc</c>
/// </summary>
public static class Ft26MvccThroughputRunner
{
    public static int Run()
    {
        Console.WriteLine("=== FT-26: MVCC single-tx write throughput ===");
        Console.WriteLine("workload, N, best_ms, ops/sec");

        const int N = 10_000;
        const int Repeats = 3;

        long bestCreateNode = Measure(Repeats, () => CreateNodeOnly(N));
        Console.WriteLine($"CreateNodeOnly,{N},{bestCreateNode},{N * 1000L / Math.Max(1, bestCreateNode)}");

        long bestCreateNodeProp = Measure(Repeats, () => CreateNodeWithProperty(N));
        Console.WriteLine($"CreateNodeWithProperty,{N},{bestCreateNodeProp},{N * 1000L / Math.Max(1, bestCreateNodeProp)}");

        long bestCreateRel = Measure(Repeats, () => CreateRelationship(N));
        Console.WriteLine($"CreateRelationship,{N},{bestCreateRel},{N * 1000L / Math.Max(1, bestCreateRel)}");

        return 0;
    }

    private static long Measure(int repeats, Func<long> body)
    {
        long best = long.MaxValue;
        for (int i = 0; i < repeats; i++)
        {
            long ms = body();
            if (ms < best) best = ms;
        }
        return best;
    }

    private static long CreateNodeOnly(int n)
    {
        string dir = BenchTempDir.Create("ft26_mvcc_node");
        try
        {
            using var db = GraphDatabase.Open(dir);
            var sw = Stopwatch.StartNew();
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < n; i++)
                    tx.CreateNode("X");
                tx.Commit();
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        finally { TryDelete(dir); }
    }

    private static long CreateNodeWithProperty(int n)
    {
        string dir = BenchTempDir.Create("ft26_mvcc_nodeprop");
        try
        {
            using var db = GraphDatabase.Open(dir);
            var sw = Stopwatch.StartNew();
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < n; i++)
                {
                    var nid = tx.CreateNode("X");
                    tx.SetProperty(nid, "i", PropertyValue.FromInt64(i));
                }
                tx.Commit();
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        finally { TryDelete(dir); }
    }

    private static long CreateRelationship(int n)
    {
        string dir = BenchTempDir.Create("ft26_mvcc_rel");
        try
        {
            using var db = GraphDatabase.Open(dir);
            using (var seed = db.BeginTransaction())
            {
                _ = seed.CreateNode("A"); // NodeId(0)
                _ = seed.CreateNode("B"); // NodeId(1)
                seed.Commit();
            }
            var sw = Stopwatch.StartNew();
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < n; i++)
                    tx.CreateRelationship(new NodeId(0), new NodeId(1), "R");
                tx.Commit();
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        finally { TryDelete(dir); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
