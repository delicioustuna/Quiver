// 起動方法: Program.cs に以下を追加
//   if (args[0] == "--spike-b") return Quiver.Benchmarks.Standalone.Dev.SpikeBReadPathRunner.Run();
using System.Diagnostics;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Standalone.Dev;

/// <summary>
/// タスク B の Spike B1: 非bulk 読取経路 (Edge linked-list) の per-edge コスト配分。
/// `tx.EnumerateEdges` は bulk-load 済みデータでも adjacency block を使わず linked-list を
/// 辿る (= 非bulk DB の読取経路と同型)。各 edge で <c>VersionedEdgeStore.Read</c> を呼び、
/// 内部で <c>TryReadHeadRaw</c> (構造) + <c>TryReadVisible</c> (MVCC 可視性) の **2 回 pin** +
/// 45B ToArray ×2 (1 つは破棄) を行う。
///
/// B1 の狙い: baseline ns/edge と **per-edge allocated bytes** を確定し、adjacency block (floor) と
/// 比較して MVCC linked-list 経路の overhead を数値で押さえる。B2 の前後差分でその配分を帰属させる。
///
/// 起動: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --spike-b</c>
/// </summary>
public static class SpikeBReadPathRunner
{
    public static int Run()
    {
        Console.WriteLine("=== Spike B1: linked-list 1-hop read path (per-edge cost + alloc) ===");
        Measure(10);
        Measure(100);
        Console.WriteLine("  (linked-list = tx.EnumerateEdges → VersionedEdgeStore.Read/edge");
        Console.WriteLine("   現状 Read/edge = 2× page pin (TryReadHeadRaw + TryReadVisible) + 45B ToArray ×2 (1 破棄)。");
        Console.WriteLine("   adj block = floor。kill criterion: linked-list 1-hop ≤ 0.8 µs/edge。)");
        return 0;
    }

    private static void Measure(int degree)
    {
        string dir = BenchTempDir.Create("spike_b");
        try
        {
            {
                using var db0 = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));
                using var loader = db0.BeginBulkLoad(buildAdjacencyIndex: true);
                loader.AppendVertex(new VertexId(0), new LabelId(0));
                for (int i = 1; i <= degree; i++)
                {
                    loader.AppendVertex(new VertexId(i), new LabelId(1));
                    loader.AppendEdge(new EdgeId(i - 1),
                        new VertexId(0), new VertexId(i), new EdgeTypeId(0));
                }
                loader.Commit();
            }
            using var db = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var hub = new VertexId(0);
            using var tx = db.BeginWriteTransaction();
            var adj = tx.AsInternal().AdjacencySegments!;
            var buf = new AdjacencyEntry[Math.Max(1024, degree + 16)];

            int LinkedScan()
            {
                int c = 0;
                var en = tx.EnumerateEdges(hub, Direction.Outgoing);
                while (en.MoveNext()) c++;
                return c;
            }
            int AdjScan() => adj.ReadEdges(hub, Direction.Outgoing, null, buf);

            double linkedNs = TimePerOp(LinkedScan, degree);
            double adjNs = TimePerOp(AdjScan, degree);
            long linkedAllocPerEdge = AllocPerOp(LinkedScan) / degree;

            Console.WriteLine($"  deg={degree,-4} linked-list {linkedNs / 1000:F3} µs/edge ({linkedNs:F1} ns) | " +
                              $"adj {adjNs:F1} ns/edge | ratio {linkedNs / Math.Max(0.01, adjNs):F1}× | " +
                              $"alloc {linkedAllocPerEdge} B/edge");
        }
        finally { BenchTempDir.Delete(dir); }
    }

    private static double TimePerOp(Func<int> op, int unit, int minOps = 200_000)
    {
        for (int i = 0; i < 1000; i++) op(); // warmup
        double best = double.MaxValue;
        for (int block = 0; block < 3; block++)
        {
            var sw = Stopwatch.StartNew();
            long total = 0;
            for (int i = 0; i < minOps; i++) total += op();
            sw.Stop();
            if (total < 0) Console.Write("");
            double nsPerCall = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / minOps;
            double nsPerUnit = nsPerCall / Math.Max(1, unit);
            if (nsPerUnit < best) best = nsPerUnit;
        }
        return best;
    }

    private static long AllocPerOp(Func<int> op, int iters = 20_000)
    {
        for (int i = 0; i < 1000; i++) op(); // warmup / JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        long sink = 0;
        for (int i = 0; i < iters; i++) sink += op();
        if (sink < 0) Console.Write("");
        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / iters;
    }
}
