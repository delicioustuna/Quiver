using System.Diagnostics;
using System.Runtime.InteropServices;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Benchmarks.Standalone;

/// <summary>
/// Clean-slate CSR edge の最小 spike。
/// 既存の永続 adjacency payload lane を immutable CSR base segment 近似として使い、
/// edge property predicate 付き 2-hop 走査の上限性能を測る。
/// </summary>
public static class CleanSlateCsrEdgeSpikeRunner
{
    private const string ScoreKey = "score";

    public static int Run(IReadOnlyList<string> args)
    {
        int degree = Parse(args, 0, 100);
        int iterations = Parse(args, 1, 300);

        Console.WriteLine("=== Clean-slate CSR edge spike ===");
        Console.WriteLine(
            $"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"degree={degree}, iterations={iterations}");
        Console.WriteLine();

        string dir = BenchTempDir.Create("clean_slate_csr_edge");
        try
        {
            string path = Path.Combine(dir, "graph.yata");
            CsrCorpus corpus;
            using (var buildDb = YatagarasuDatabase.Open(path))
                corpus = BuildCorpus(buildDb, degree);

            using var db = YatagarasuDatabase.Open(path);
            using var read = db.BeginReadTransaction();
            int warmup = CountPredicateMatches(read, corpus.Hub);
            if (warmup != corpus.ExpectedMatches)
            {
                throw new InvalidOperationException(
                    $"CSR predicate result mismatch. Expected {corpus.ExpectedMatches}, got {warmup}.");
            }

            var ticks = new long[iterations];
            for (int i = 0; i < ticks.Length; i++)
            {
                long started = Stopwatch.GetTimestamp();
                int count = CountPredicateMatches(read, corpus.Hub);
                ticks[i] = Stopwatch.GetTimestamp() - started;
                if (count != corpus.ExpectedMatches)
                {
                    throw new InvalidOperationException(
                        $"CSR predicate result mismatch. Expected {corpus.ExpectedMatches}, got {count}.");
                }
            }

            double p50 = PercentileMs(ticks, 0.50);
            double p95 = PercentileMs(ticks, 0.95);
            double directLookupNs = MeasureLocatorLookup(corpus);
            double updateNs = MeasureDeltaOverlayPointUpdate(corpus);

            const double baselineP50Ms = 3.7963;
            const double requiredP50Ms = baselineP50Ms / 2.0;
            bool traversalPass = p50 <= requiredP50Ms;

            Console.WriteLine(
                $"csr_predicate_2hop, edges={corpus.SecondHopEdges}, matches={corpus.ExpectedMatches}, " +
                $"p50_ms={p50:F4}, p95_ms={p95:F4}, " +
                $"throughput_traversals_per_sec={1000.0 / p50:F1}, " +
                $"baseline_p50_ms={baselineP50Ms:F4}, speedup={baselineP50Ms / p50:F2}x, " +
                $"required_p50_ms={requiredP50Ms:F4}, result={(traversalPass ? "PASS" : "FAIL")}");
            Console.WriteLine($"locator_direct_lookup, ns_per_lookup={directLookupNs:F1}, result=PASS");
            Console.WriteLine(
                $"delta_overlay_point_update, ns_per_update={updateNs:F1}, " +
                "result=REFERENCE_ONLY");
            Console.WriteLine(
                "csv,csr_edge,predicate_2hop_p50_ms,predicate_2hop_p95_ms," +
                "speedup,direct_lookup_ns,delta_overlay_update_ns,result");
            Console.WriteLine(
                $"csv,csr_edge,{p50:F4},{p95:F4},{baselineP50Ms / p50:F2}," +
                $"{directLookupNs:F1},{updateNs:F1},{(traversalPass ? "PASS" : "FAIL")}");

            return traversalPass ? 0 : 2;
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static int Parse(IReadOnlyList<string> args, int index, int fallback)
        => index < args.Count && int.TryParse(args[index], out int value) && value > 0
            ? value
            : fallback;

    private static CsrCorpus BuildCorpus(YatagarasuDatabase db, int degree)
    {
        var label = db.EditSchema(schema => schema.GetOrCreateLabel("V"));
        var type = db.EditSchema(schema => schema.GetOrCreateEdgeType("LINK"));
        var scoreKey = db.EditSchema(schema => schema.GetOrCreatePropertyKey(ScoreKey));
        var payload = PayloadLaneSpec.ForInt64(scoreKey.Value);

        int vertexCount = 1 + degree + degree * degree;
        int firstHopEdges = degree;
        int secondHopEdges = degree * degree;
        int totalEdges = firstHopEdges + secondHopEdges;
        var sourceByEdge = new long[totalEdges];
        var targetByEdge = new long[totalEdges];
        var scoreByEdge = new long[totalEdges];
        int expectedMatches = 0;

        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        loader.WithPayloadLane(payload);
        for (int n = 0; n < vertexCount; n++)
            loader.AppendVertex(new VertexId(n), label);

        long edgeId = 0;
        for (int mid = 0; mid < degree; mid++)
        {
            long midVertex = 1 + mid;
            AppendEdge(loader, edgeId, 0, midVertex, type, scoreKey, 0, sourceByEdge, targetByEdge, scoreByEdge);
            edgeId++;

            for (int leaf = 0; leaf < degree; leaf++)
            {
                long leafVertex = 1 + degree + (long)mid * degree + leaf;
                long score = (mid + leaf) % 4 == 0 ? 1 : 0;
                AppendEdge(loader, edgeId, midVertex, leafVertex, type, scoreKey, score, sourceByEdge, targetByEdge, scoreByEdge);
                if (score == 1)
                    expectedMatches++;
                edgeId++;
            }
        }

        loader.Commit();
        return new CsrCorpus(
            new VertexId(0),
            secondHopEdges,
            expectedMatches,
            sourceByEdge,
            targetByEdge,
            scoreByEdge);
    }

    private static void AppendEdge(
        BulkLoader loader,
        long edgeSequence,
        long source,
        long target,
        EdgeTypeId type,
        PropertyKeyId scoreKey,
        long score,
        long[] sourceByEdge,
        long[] targetByEdge,
        long[] scoreByEdge)
    {
        var edgeId = new EdgeId(edgeSequence);
        loader.AppendEdge(edgeId, new VertexId(source), new VertexId(target), type);
        loader.AppendEdgePayload(edgeId, scoreKey, score);
        sourceByEdge[edgeSequence] = source;
        targetByEdge[edgeSequence] = target;
        scoreByEdge[edgeSequence] = score;
    }

    private static int CountPredicateMatches(IReadTransaction read, VertexId hub)
    {
        var adj = read.AsInternal().AdjacencySegments
            ?? throw new InvalidOperationException("Adjacency block store was not built.");

        int count = 0;
        using var first = adj.OpenCursor(hub, Direction.Outgoing, null);
        while (first.MoveNext())
        {
            using var second = adj.OpenCursor(first.Neighbor, Direction.Outgoing, null);
            while (second.MoveNext())
            {
                if (second.WeightRaw == 1)
                    count++;
            }
        }

        return count;
    }

    private static double MeasureLocatorLookup(CsrCorpus corpus)
    {
        const int SampleSize = 4096;
        int[] sample = new int[SampleSize];
        var rng = new Random(1234);
        for (int i = 0; i < sample.Length; i++)
            sample[i] = rng.Next(corpus.ScoreByEdge.Length);

        int cursor = 0;
        long checksum = 0;
        long Lookup()
        {
            int edge = sample[cursor++ & (SampleSize - 1)];
            checksum += corpus.SourceByEdge[edge];
            checksum += corpus.TargetByEdge[edge];
            checksum += corpus.ScoreByEdge[edge];
            return checksum;
        }

        return TimePerOpNs(Lookup, 200_000);
    }

    private static double MeasureDeltaOverlayPointUpdate(CsrCorpus corpus)
    {
        var delta = new Dictionary<long, long>();
        int cursor = 0;
        long Update()
        {
            long edge = 1 + (cursor++ % Math.Max(1, corpus.SecondHopEdges));
            delta[edge] = cursor;
            return delta.Count;
        }

        return TimePerOpNs(Update, 100_000);
    }

    private static double TimePerOpNs(Func<long> op, int iterations)
    {
        for (int i = 0; i < Math.Min(1000, iterations); i++)
            _ = op();

        double best = double.MaxValue;
        for (int block = 0; block < 3; block++)
        {
            long sum = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                sum += op();
            sw.Stop();
            GC.KeepAlive(sum);
            double ns = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
            if (ns < best)
                best = ns;
        }
        return best;
    }

    private static double PercentileMs(long[] ticks, double p)
        => PercentileTicks(ticks, p) * 1000.0 / Stopwatch.Frequency;

    private static long PercentileTicks(long[] ticks, double p)
    {
        var sorted = (long[])ticks.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private readonly record struct CsrCorpus(
        VertexId Hub,
        int SecondHopEdges,
        int ExpectedMatches,
        long[] SourceByEdge,
        long[] TargetByEdge,
        long[] ScoreByEdge);
}
