// 起動方法: Program.cs に以下を追加
//   if (args[0] == "--spike-a") return Quiver.Benchmarks.Standalone.Dev.SpikeAPlanCompileRunner.Run();
using System.Diagnostics;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Standalone.Dev;

/// <summary>
/// タスク A の Spike A0: クエリ DSL のクエリごと固定費 (~60µs) が
/// LogicalOptimizer.Optimize / PhysicalPlanner.Plan / traversal 構築 / 実行 の
/// どこに乗っているかを配分計測する。InternalsVisibleTo("Quiver.Benchmarks") 経由で
/// LogicalOptimizer / PhysicalPlanner / GraphTraversal の internal を直接叩く。
///
/// 起動: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --spike-a</c>
/// </summary>
public static class SpikeAPlanCompileRunner
{
    public static int Run()
    {
        const int Degree = 100;
        Console.WriteLine("=== Spike A0: query compile cost breakdown (1-hop, degree=100) ===");

        string dir = BenchTempDir.Create("spike_a");
        try
        {
            {
                using var db0 = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));
                using var loader = db0.BeginBulkLoad(buildAdjacencyIndex: true);
                loader.AppendVertex(new VertexId(0), new LabelId(0));
                for (int i = 1; i <= Degree; i++)
                {
                    loader.AppendVertex(new VertexId(i), new LabelId(1));
                    loader.AppendEdge(new EdgeId(i - 1), new VertexId(0), new VertexId(i), new EdgeTypeId(0));
                }
                loader.Commit();
            }
            using var db = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var hub = new VertexId(0);
            using var tx = db.BeginWriteTransaction();
            var schema = db.Schema;
            var g = tx.Query;

            // 事前に 1 本作って LogicalOp / optimized plan を取り出す (build/optimize/plan を個別計測するため)。
            var trav = g.Vertex(hub).Out();
            LogicalOp plan = trav._plan;
            LogicalOp optimized = LogicalOptimizer.Optimize(plan, trav._stats, schema);

            // 各段を ns/query で計測。
            double tBuild = Time(() => { var t = g.Vertex(hub).Out(); return t._plan is not null ? 1 : 0; });
            double tOptimize = Time(() => { var o = LogicalOptimizer.Optimize(plan, trav._stats, schema); return o is not null ? 1 : 0; });
            double tPlan = Time(() => { var op = PhysicalPlanner.Plan(optimized, schema); return op is not null ? 1 : 0; });
            double tCompile = Time(() => { var op = PhysicalPlanner.Plan(LogicalOptimizer.Optimize(plan, trav._stats, schema), schema); return op is not null ? 1 : 0; });
            double tFull = Time(() =>
            {
                int c = 0;
                using var cur = g.Vertex(hub).Out().AsCursor();
                while (cur.MoveNext()) c++;
                return c;
            });

            // アロケーション (full query 1 回あたり)。
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            const int AllocIters = 10_000;
            for (int i = 0; i < AllocIters; i++)
            {
                using var cur = g.Vertex(hub).Out().AsCursor();
                while (cur.MoveNext()) { }
            }
            long allocPerQuery = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / AllocIters;

            Console.WriteLine($"  traversal build  : {tBuild / 1000:F2} µs/query");
            Console.WriteLine($"  Optimize         : {tOptimize / 1000:F2} µs/query");
            Console.WriteLine($"  PhysicalPlanner  : {tPlan / 1000:F2} µs/query");
            Console.WriteLine($"  Compile (opt+plan): {tCompile / 1000:F2} µs/query");
            Console.WriteLine($"  FULL (build+compile+open+iterate 100 edges): {tFull / 1000:F2} µs/query");
            Console.WriteLine($"  → exec+rest (FULL - build - compile): {(tFull - tBuild - tCompile) / 1000:F2} µs/query");
            Console.WriteLine($"  allocated        : {allocPerQuery:N0} bytes/query");
            Console.WriteLine("  (A0 知見: per-query 固定費 ~0.4µs。残りは per-row 世代 stamping = version sidecar read が支配。)");

            return 0;
        }
        finally { BenchTempDir.Delete(dir); }
    }

    private static double Time(Func<int> op, int iters = 200_000)
    {
        for (int i = 0; i < 2000; i++) op(); // warmup
        double best = double.MaxValue;
        for (int block = 0; block < 3; block++)
        {
            var sw = Stopwatch.StartNew();
            long acc = 0;
            for (int i = 0; i < iters; i++) acc += op();
            sw.Stop();
            if (acc < 0) Console.Write("");
            double ns = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iters;
            if (ns < best) best = ns;
        }
        return best;
    }
}
