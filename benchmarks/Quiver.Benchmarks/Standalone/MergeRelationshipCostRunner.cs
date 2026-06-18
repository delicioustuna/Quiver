using System.Diagnostics;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Standalone;

public static class MergeRelationshipCostRunner
{
    public static int Run()
    {
        Console.WriteLine("=== QP-3: MergeRelationship degree-dependent cost ===");
        Console.WriteLine($"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
                          $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        Console.WriteLine("[1] MergeRelationship hit (existing edge) — µs/call vs out-degree of same type");
        Console.WriteLine("  degree, µs/call, ns/call");
        foreach (int deg in new[] { 1, 10, 50, 100, 500, 1000 })
            MeasureHit(deg);

        Console.WriteLine();
        Console.WriteLine("[2] MergeRelationship miss (new edge, high fan-out from other targets) — µs/call vs out-degree");
        Console.WriteLine("  degree, µs/call, ns/call");
        foreach (int deg in new[] { 0, 10, 50, 100, 500, 1000 })
            MeasureMiss(deg);

        Console.WriteLine();
        Console.WriteLine("[3] CreateRelationship baseline (no existence check) — µs/call");
        MeasureCreateBaseline();

        Console.WriteLine();
        Console.WriteLine("[4] Batch MergeRelationship (typed sink, N sources × M targets) — ms total, µs/pair");
        Console.WriteLine("  sources, targets, degree_before, ms, µs/pair");
        MeasureBatchSink(10, 10, 0);
        MeasureBatchSink(10, 10, 100);
        MeasureBatchSink(50, 50, 0);
        MeasureBatchSink(50, 50, 100);

        return 0;
    }

    private static void MeasureHit(int degree)
    {
        string dir = BenchTempDir.Create("qp3_hit");
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            NodeId src, target;
            using (var seed = db.BeginTransaction())
            {
                src = seed.CreateNode("A");
                target = seed.CreateNode("B");
                seed.CreateRelationship(src, target, "R");
                for (int i = 1; i < degree; i++)
                {
                    var other = seed.CreateNode("B");
                    seed.CreateRelationship(src, other, "R");
                }
                seed.Commit();
            }

            // warmup
            for (int i = 0; i < 500; i++)
            {
                using var tx = db.BeginTransaction();
                tx.MergeRelationship(src, target, "R");
                // no commit — discard
            }

            // measure: single MergeRelationship call that hits existing edge
            const int Iters = 5000;
            double bestNs = double.MaxValue;
            for (int block = 0; block < 5; block++)
            {
                using var tx = db.BeginTransaction();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < Iters; i++)
                {
                    var (_, created) = tx.MergeRelationship(src, target, "R");
                    Debug.Assert(!created);
                }
                sw.Stop();
                double ns = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / Iters;
                if (ns < bestNs) bestNs = ns;
            }
            Console.WriteLine($"  {degree}, {bestNs / 1000:F3}, {bestNs:F1}");
        }
        finally { BenchTempDir.Delete(dir); }
    }

    private static void MeasureMiss(int existingDegree)
    {
        // miss = full scan (no match) + CreateRelationship。
        // 単一 tx 内で計測するが、miss は辺を作るため degree が漸増する。
        // delta を base degree の 10% 以内に抑え、平均 degree ≈ base で近似する。
        string dir = BenchTempDir.Create("qp3_miss");
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            NodeId src;
            using (var seed = db.BeginTransaction())
            {
                src = seed.CreateNode("A");
                for (int i = 0; i < existingDegree; i++)
                {
                    var other = seed.CreateNode("B");
                    seed.CreateRelationship(src, other, "R");
                }
                seed.Commit();
            }

            int iters = existingDegree < 10 ? 5000 : Math.Max(200, existingDegree / 10);

            // warmup
            {
                using var tx = db.BeginTransaction();
                for (int w = 0; w < Math.Min(200, iters); w++)
                {
                    var t = tx.CreateNode("B");
                    tx.MergeRelationship(src, t, "R");
                }
            }

            double bestNs = double.MaxValue;
            for (int block = 0; block < 5; block++)
            {
                using var tx = db.BeginTransaction();
                var targets = new NodeId[iters];
                for (int i = 0; i < iters; i++)
                    targets[i] = tx.CreateNode("B");

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iters; i++)
                    tx.MergeRelationship(src, targets[i], "R");
                sw.Stop();
                double ns = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iters;
                if (ns < bestNs) bestNs = ns;
            }
            Console.WriteLine($"  {existingDegree}, {bestNs / 1000:F3}, {bestNs:F1}");
        }
        finally { BenchTempDir.Delete(dir); }
    }

    private static void MeasureCreateBaseline()
    {
        string dir = BenchTempDir.Create("qp3_create");
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            using (var seed = db.BeginTransaction()) { seed.CreateNode("A"); seed.CreateNode("B"); seed.Commit(); }
            var src = new NodeId(0);
            var tgt = new NodeId(1);

            for (int i = 0; i < 1000; i++)
            {
                using var tx = db.BeginTransaction();
                tx.CreateRelationship(src, tgt, "R");
            }

            const int Iters = 10000;
            double bestNs = double.MaxValue;
            for (int block = 0; block < 5; block++)
            {
                using var tx = db.BeginTransaction();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < Iters; i++)
                    tx.CreateRelationship(src, tgt, "R");
                sw.Stop();
                double ns = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / Iters;
                if (ns < bestNs) bestNs = ns;
            }
            Console.WriteLine($"  CreateRelationship (no check), {bestNs / 1000:F3} µs/call, {bestNs:F1} ns/call");
        }
        finally { BenchTempDir.Delete(dir); }
    }

    private static void MeasureBatchSink(int sources, int targets, int preExistingDegree)
    {
        string dir = BenchTempDir.Create("qp3_batch");
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            var srcIds = new NodeId[sources];
            var tgtIds = new NodeId[targets];
            using (var seed = db.BeginTransaction())
            {
                for (int i = 0; i < sources; i++)
                {
                    srcIds[i] = seed.CreateNode("S");
                    seed.SetProperty(srcIds[i], "Name", PropertyValue.FromString("S" + i));
                }
                for (int j = 0; j < targets; j++)
                {
                    tgtIds[j] = seed.CreateNode("T");
                    seed.SetProperty(tgtIds[j], "Name", PropertyValue.FromString("T" + j));
                }
                for (int i = 0; i < sources; i++)
                    for (int d = 0; d < preExistingDegree; d++)
                    {
                        var dummy = seed.CreateNode("T");
                        seed.CreateRelationship(srcIds[i], dummy, "R");
                    }
                seed.Commit();
            }

            // warmup
            for (int w = 0; w < 3; w++)
            {
                using var tx = db.BeginTransaction();
                for (int i = 0; i < sources; i++)
                    for (int j = 0; j < targets; j++)
                        tx.MergeRelationship(srcIds[i], tgtIds[j], "R");
            }

            const int Repeats = 5;
            double bestMs = double.MaxValue;
            for (int r = 0; r < Repeats; r++)
            {
                using var tx = db.BeginTransaction();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < sources; i++)
                    for (int j = 0; j < targets; j++)
                        tx.MergeRelationship(srcIds[i], tgtIds[j], "R");
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms < bestMs) bestMs = ms;
            }
            int pairs = sources * targets;
            Console.WriteLine($"  {sources}, {targets}, {preExistingDegree}, {bestMs:F2}, {bestMs * 1000 / pairs:F2}");
        }
        finally { BenchTempDir.Delete(dir); }
    }
}
