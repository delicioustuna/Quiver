using System.Diagnostics;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;

// 起動方法: Program.cs に以下を追加
//   if (args[0] == "--ft26-mvcc") return Quiver.Benchmarks.Standalone.Dev.Ft26MvccThroughputRunner.Run();
namespace Quiver.Benchmarks.Standalone.Dev;

/// <summary>
/// MVCC 有効時の single-tx 書き込みスループット絶対値を計測するランナー。
///
/// <para>
///  完了条件「単一 tx workload で書き込みスループットが MVCC なし比 80% 以上」を
/// 検証するため、本ランナーで現行 (MVCC 有) の絶対値を取り、別 worktree で pre-
/// commit (754c2bd 等) を build → 同じランナーを移植して同 workload を回し、ratio を求める。
/// </para>
///
/// <para>
///  受け入れ時の実測値 (2026-05-25、Windows 11、best-of-3、N=10000):
/// <list type="bullet">
///   <item>CreateVertexOnly          : pre 370k/s →  345k/s = <b>93%</b></item>
///   <item>CreateVertexWithProperty  : pre 128k/s →  115k/s = <b>90%</b></item>
///   <item>CreateEdge      : pre 116k/s →  106k/s = <b>91%</b></item>
/// </list>
/// いずれも DoD 「80% 以上」を満たす。
/// </para>
///
/// <para>
/// 計測する workload (いずれも single-thread / single tx):
/// <list type="bullet">
///   <item>CreateVertexOnly: <c>N</c> Vertex create + commit</item>
///   <item>CreateVertexWithProperty: <c>N</c> Vertex create + property set + commit</item>
///   <item>CreateEdge: <c>N</c> edge create (固定 2 Vertex間) + commit</item>
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
        Console.WriteLine("=== MVCC single-tx write throughput ===");
        Console.WriteLine("workload, N, best_ms, ops/sec");

        const int N = 10_000;
        const int Repeats = 3;

        long bestCreateVertex = Measure(Repeats, () => CreateVertexOnly(N));
        Console.WriteLine($"CreateVertexOnly,{N},{bestCreateVertex},{N * 1000L / Math.Max(1, bestCreateVertex)}");

        long bestCreateVertexProp = Measure(Repeats, () => CreateVertexWithProperty(N));
        Console.WriteLine($"CreateVertexWithProperty,{N},{bestCreateVertexProp},{N * 1000L / Math.Max(1, bestCreateVertexProp)}");

        long bestCreateEdge = Measure(Repeats, () => CreateEdge(N));
        Console.WriteLine($"CreateEdge,{N},{bestCreateEdge},{N * 1000L / Math.Max(1, bestCreateEdge)}");

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

    private static long CreateVertexOnly(int n)
    {
        string dir = BenchTempDir.Create("ft26_mvcc_vertex");
        try
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
            var sw = Stopwatch.StartNew();
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < n; i++)
                    tx.CreateVertex("X");
                tx.Commit();
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        finally { TryDelete(dir); }
    }

    private static long CreateVertexWithProperty(int n)
    {
        string dir = BenchTempDir.Create("ft26_mvcc_vertexprop");
        try
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
            var sw = Stopwatch.StartNew();
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < n; i++)
                {
                    var nid = tx.CreateVertex("X");
                    tx.SetProperty(nid, "i", PropertyValue.FromInt64(i));
                }
                tx.Commit();
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        finally { TryDelete(dir); }
    }

    private static long CreateEdge(int n)
    {
        string dir = BenchTempDir.Create("ft26_mvcc_edge");
        try
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
            using (var seed = db.BeginTransaction())
            {
                _ = seed.CreateVertex("A"); // VertexId(0)
                _ = seed.CreateVertex("B"); // VertexId(1)
                seed.Commit();
            }
            var sw = Stopwatch.StartNew();
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < n; i++)
                    tx.CreateEdge(new VertexId(0), new VertexId(1), "R");
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
