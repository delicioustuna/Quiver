using System.Diagnostics;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// 基本性能 (README「性能目標」表) の絶対値を 1 ランで計測するランナー。
/// BenchmarkDotNet の子プロセス起動を経由せず、warmup + best-of-N の Stopwatch 計測で
/// 数秒〜十数秒で結果を出す (FT-26/FT-27 等の standalone runner と同じ流儀)。
///
/// 計測項目 (README の表に対応):
///   - 書き込み (single-tx 償却 µs/op): CreateNode / CreateNode+SetProperty / CreateRelationship
///   - durable commit レイテンシ (ms/commit): 1 op = 1 commit を直列で繰り返したときの WAL flush 律速値
///   - 読み取り (warm ns/op): EnumerateRelationships(隣接10件) / 1-hop linked-list / 1-hop AdjacencyBlock
///   - BFS 2-hop (ハブ degree=100, AdjacencyBlock): 1 探索あたりの ms
///   - クエリラッパオーバーヘッド (%): raw EnumerateRelationships vs g.Node().Out() の 1-hop
///   - BulkLoader: 100k edge を bulk vs 通常 TX(batch 1000) でロードした比
///
/// 起動方法: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --basic-perf</c>
/// </summary>
public static class BasicPerfRunner
{
    public static int Run()
    {
        Console.WriteLine("=== Basic performance (README 性能目標) ===");
        Console.WriteLine($"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
                          $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        WriteThroughput();
        Console.WriteLine();
        DurableCommitLatency();
        Console.WriteLine();
        ReadMicro();
        Console.WriteLine();
        TwoHop();
        Console.WriteLine();
        WrapperOverhead();
        Console.WriteLine();
        BulkVsTx();

        return 0;
    }

    // ── 書き込み: single-tx 償却 µs/op ───────────────────────────────────────
    private static void WriteThroughput()
    {
        const int N = 50_000, Repeats = 5;
        Console.WriteLine("[write, single-tx amortized]  workload, N, best_ms, us/op, ops/sec");

        long node = Measure(Repeats, () => CreateNodes(N, withProp: false));
        Report("CreateNode", N, node);

        long nodeProp = Measure(Repeats, () => CreateNodes(N, withProp: true));
        Report("CreateNode+SetProperty", N, nodeProp);

        long rel = Measure(Repeats, () => CreateRels(N));
        Report("CreateRelationship", N, rel);

        static void Report(string name, int n, long ms)
            => Console.WriteLine($"  {name}, {n}, {ms}, {(double)ms * 1000 / n:F3}, {n * 1000L / Math.Max(1, ms):N0}");
    }

    private static long CreateNodes(int n, bool withProp)
    {
        string dir = BenchTempDir.Create("basic_node");
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var sw = Stopwatch.StartNew();
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < n; i++)
                {
                    var id = tx.CreateNode("X");
                    if (withProp) tx.SetProperty(id, "i", PropertyValue.FromInt64(i));
                }
                tx.Commit();
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        finally { BenchTempDir.Delete(dir); }
    }

    private static long CreateRels(int n)
    {
        string dir = BenchTempDir.Create("basic_rel");
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            using (var seed = db.BeginTransaction())
            {
                seed.CreateNode("A");
                seed.CreateNode("B");
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
        finally { BenchTempDir.Delete(dir); }
    }

    // ── durable commit レイテンシ: 1 op = 1 commit ───────────────────────────
    private static void DurableCommitLatency()
    {
        const int Commits = 2_000;
        Console.WriteLine("[durable commit, 1 node = 1 commit, single-thread]  commits, total_ms, ms/commit, commits/sec");
        long best = Measure(3, () =>
        {
            string dir = BenchTempDir.Create("basic_commit");
            try
            {
                using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < Commits; i++)
                {
                    using var tx = db.BeginTransaction();
                    tx.CreateNode("X");
                    tx.Commit();
                }
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            finally { BenchTempDir.Delete(dir); }
        });
        Console.WriteLine($"  durable, {Commits}, {best}, {(double)best / Commits:F3}, {Commits * 1000L / Math.Max(1, best):N0}");
    }

    // ── 読み取り: warm ns/op ─────────────────────────────────────────────────
    private static void ReadMicro()
    {
        Console.WriteLine("[read, warm]  metric, degree, ns/op (linked / adjblock)");
        MeasureOneHop(10);
        MeasureOneHop(100);
    }

    private static void MeasureOneHop(int degree)
    {
        string dir = BenchTempDir.Create("basic_1hop");
        try
        {
            {
                using var db0 = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
                using var loader = db0.BeginBulkLoad(buildAdjacencyIndex: true);
                loader.AppendNode(new NodeId(0), new LabelId(0));
                for (int i = 1; i <= degree; i++)
                {
                    loader.AppendNode(new NodeId(i), new LabelId(1));
                    loader.AppendRelationship(new RelationshipId(i - 1),
                        new NodeId(0), new NodeId(i), new RelationshipTypeId(0));
                }
                loader.Commit();
            }
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var hub = new NodeId(0);
            using var tx = db.BeginTransaction();
            var adj = tx.AsInternal().AdjacencyBlocks!;
            var buf = new AdjacencyEntry[Math.Max(1024, degree + 16)];

            int LinkedScan()
            {
                int c = 0;
                var en = tx.EnumerateRelationships(hub, Direction.Outgoing);
                while (en.MoveNext()) c++;
                return c;
            }
            int AdjScan() => adj.ReadEdges(hub, Direction.Outgoing, null, buf);

            double linkedNs = TimePerOp(LinkedScan, degree);
            double adjNs = TimePerOp(AdjScan, degree);
            Console.WriteLine($"  1-hop scan, {degree}, {linkedNs:F1} / {adjNs:F1}");
        }
        finally { BenchTempDir.Delete(dir); }
    }

    // ── BFS 2-hop (ハブ degree=100, AdjacencyBlock) ──────────────────────────
    private static void TwoHop()
    {
        const int Degree = 100;
        Console.WriteLine("[BFS 2-hop, AdjacencyBlock]  hubDegree, leaves, ms/traversal");
        string dir = BenchTempDir.Create("basic_2hop");
        try
        {
            {
                using var db0 = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
                using var loader = db0.BeginBulkLoad(buildAdjacencyIndex: true);
                loader.AppendNode(new NodeId(0), new LabelId(0));
                long relId = 0;
                for (int m = 0; m < Degree; m++)
                {
                    long midId = 1 + m;
                    loader.AppendNode(new NodeId(midId), new LabelId(1));
                    loader.AppendRelationship(new RelationshipId(relId++), new NodeId(0), new NodeId(midId), new RelationshipTypeId(0));
                    for (int l = 0; l < Degree; l++)
                    {
                        long leafId = 1 + Degree + (long)m * Degree + l;
                        loader.AppendNode(new NodeId(leafId), new LabelId(2));
                        loader.AppendRelationship(new RelationshipId(relId++), new NodeId(midId), new NodeId(leafId), new RelationshipTypeId(0));
                    }
                }
                loader.Commit();
            }
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var hub = new NodeId(0);
            using var tx = db.BeginTransaction();
            var adj = tx.AsInternal().AdjacencyBlocks!;
            var l1 = new AdjacencyEntry[8192];
            var l2 = new AdjacencyEntry[8192];

            int TwoHopScan()
            {
                int c = 0;
                int mids = adj.ReadEdges(hub, Direction.Outgoing, null, l1);
                for (int i = 0; i < mids; i++)
                    c += adj.ReadEdges(l1[i].NeighborId, Direction.Outgoing, null, l2);
                return c;
            }
            double nsPerOp = TimePerOp(TwoHopScan, 1, minOps: 200);
            Console.WriteLine($"  2-hop, {Degree}, {Degree * Degree}, {nsPerOp / 1_000_000:F4}");
        }
        finally { BenchTempDir.Delete(dir); }
    }

    // ── クエリラッパオーバーヘッド (%) ───────────────────────────────────────
    private static void WrapperOverhead()
    {
        const int Degree = 100;
        Console.WriteLine("[query wrapper overhead]  raw_ns, wrapped_ns, overhead_%");
        string dir = BenchTempDir.Create("basic_wrap");
        try
        {
            {
                using var db0 = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
                using var loader = db0.BeginBulkLoad(buildAdjacencyIndex: true);
                loader.AppendNode(new NodeId(0), new LabelId(0));
                for (int i = 1; i <= Degree; i++)
                {
                    loader.AppendNode(new NodeId(i), new LabelId(1));
                    loader.AppendRelationship(new RelationshipId(i - 1), new NodeId(0), new NodeId(i), new RelationshipTypeId(0));
                }
                loader.Commit();
            }
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var hub = new NodeId(0);
            using var tx = db.BeginTransaction();
            var g = tx.G(db.Schema);
            var adj = tx.AsInternal().AdjacencyBlocks!;
            var buf = new AdjacencyEntry[Degree + 16];

            // 同一の高速アクセス経路 (adjacency block) を、生 API と traversal DSL で叩いて
            // DSL/operator 層が足すオーバーヘッドを測る。raw=adj.ReadEdges、wrapped=g.Node().Out()。
            int Raw() => adj.ReadEdges(hub, Direction.Outgoing, null, buf);
            int Wrapped()
            {
                int c = 0;
                using var cur = g.Node(hub).Out().AsCursor();
                while (cur.MoveNext()) c++;
                return c;
            }

            double rawNs = TimePerOp(Raw, Degree);
            double wrapNs = TimePerOp(Wrapped, Degree);
            double pct = (wrapNs - rawNs) / rawNs * 100.0;
            Console.WriteLine($"  raw_adj {rawNs * Degree:F0}ns / wrapped {wrapNs * Degree:F0}ns (per-edge {rawNs:F1} / {wrapNs:F1}), {pct:F1}%");
        }
        finally { BenchTempDir.Delete(dir); }
    }

    // ── BulkLoader vs 通常 TX ────────────────────────────────────────────────
    private static void BulkVsTx()
    {
        const int Edges = 100_000;
        int nodes = Edges / 10;
        Console.WriteLine("[bulk vs tx]  edges, bulk_ms, tx_ms, speedup");
        long bulk = Measure(3, () => BulkLoad(Edges, nodes));
        long txm = Measure(2, () => TxLoad(Edges, nodes));
        Console.WriteLine($"  {Edges}, {bulk}, {txm}, {(double)txm / Math.Max(1, bulk):F1}x");
    }

    private static long BulkLoad(int edges, int nodes)
    {
        string dir = BenchTempDir.Create("basic_bulk");
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var label = db.Schema.GetOrCreateLabel("V");
            var rt = db.Schema.GetOrCreateRelationshipType("R");
            var sw = Stopwatch.StartNew();
            using (var bulk = db.BeginBulkLoad())
            {
                for (long i = 0; i < nodes; i++) bulk.AppendNode(new NodeId(i), label);
                var rng = new Random(42);
                for (long i = 0; i < edges; i++)
                    bulk.AppendRelationship(new RelationshipId(i), new NodeId(rng.Next(nodes)), new NodeId(rng.Next(nodes)), rt);
                bulk.Commit();
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        finally { BenchTempDir.Delete(dir); }
    }

    private static long TxLoad(int edges, int nodes)
    {
        const int Batch = 1_000;
        string dir = BenchTempDir.Create("basic_txload");
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var nodeIds = new NodeId[nodes];
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < nodes; i += Batch)
            {
                using var tx = db.BeginTransaction();
                int end = Math.Min(i + Batch, nodes);
                for (int j = i; j < end; j++) nodeIds[j] = tx.CreateNode("V");
                tx.Commit();
            }
            var rng = new Random(42);
            for (int i = 0; i < edges; i += Batch)
            {
                using var tx = db.BeginTransaction();
                int end = Math.Min(i + Batch, edges);
                for (int j = i; j < end; j++)
                    tx.CreateRelationship(nodeIds[rng.Next(nodes)], nodeIds[rng.Next(nodes)], "R");
                tx.Commit();
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        finally { BenchTempDir.Delete(dir); }
    }

    // ── 計測ヘルパ ───────────────────────────────────────────────────────────
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

    /// <summary>
    /// <paramref name="op"/> を warmup 後に多数回回し、1 回あたりの ns を返す。
    /// <paramref name="unit"/> は 1 op が処理する論理件数 (per-edge 値が欲しいとき degree を渡す。
    /// per-traversal 値が欲しいときは 1)。best-of-3 ブロックで GC 揺らぎを抑える。
    /// </summary>
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
            if (total < 0) Console.Write(""); // prevent dead-code elimination
            double nsPerCall = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / minOps;
            double nsPerUnit = nsPerCall / Math.Max(1, unit);
            if (nsPerUnit < best) best = nsPerUnit;
        }
        return best;
    }
}
