using System.Diagnostics;
using System.Runtime.InteropServices;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// CSR edge を既存 product path に寄せて検証する runner。
/// 書き込みは公開 QuiverDatabase API、merge は CompactAdjacency、読み取りは payload lane と row property を照合する。
/// </summary>
public static class CleanSlateCsrProductIntegrationRunner
{
    private const string ScoreKey = "score";
    private const double RequiredReadPathP50Ms = 1.8982;
    private const double RequiredMergeGateMs = 2_000.0;

    public static int Run(IReadOnlyList<string> args)
    {
        int degree = Parse(args, 0, 100);
        int iterations = Parse(args, 1, 300);
        int mutationCount = Parse(args, 2, 100);

        Console.WriteLine("=== Clean-slate CSR product integration ===");
        Console.WriteLine(
            $"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"degree={degree}, iterations={iterations}, mutation_count={mutationCount}");
        Console.WriteLine();

        string dir = BenchTempDir.Create("clean_slate_csr_integrated");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "graph.quiver");
            var corpus = BuildBulkGraph(path, degree);
            SeedEdgeProperties(path, corpus);
            var mutations = ApplyDeterministicMutations(path, corpus, mutationCount);

            double compactMs;
            using (var db = QuiverDatabase.Open(path))
            {
                var sw = Stopwatch.StartNew();
                db.CompactAdjacency();
                sw.Stop();
                compactMs = sw.Elapsed.TotalMilliseconds;
            }

            using (var db = QuiverDatabase.Open(path))
            using (var tx = db.BeginReadOnlyTransaction())
            {
                var validation = ValidatePayloadAgainstRowPath(tx, corpus.Hub);
                if (!validation.Passed)
                {
                    Console.WriteLine(
                        $"validation, payload_matches={validation.PayloadMatches}, " +
                        $"row_matches={validation.RowMatches}, mismatches={validation.Mismatches}, result=FAIL");
                    return 2;
                }

                int warmup = CountPayloadMatches(tx, corpus.Hub);
                if (warmup != validation.RowMatches)
                    throw new InvalidOperationException("Warmup count diverged from row-path validation.");

                var ticks = new long[iterations];
                for (int i = 0; i < ticks.Length; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    int count = CountPayloadMatches(tx, corpus.Hub);
                    ticks[i] = Stopwatch.GetTimestamp() - started;
                    if (count != validation.RowMatches)
                        throw new InvalidOperationException("Payload count changed during integration benchmark.");
                }

                double p50 = PercentileMs(ticks, 0.50);
                double p95 = PercentileMs(ticks, 0.95);
                bool readPass = p50 <= RequiredReadPathP50Ms;

                Console.WriteLine(
                    $"validation, payload_matches={validation.PayloadMatches}, row_matches={validation.RowMatches}, " +
                    $"mismatches={validation.Mismatches}, result=PASS");
                Console.WriteLine(
                    $"mutation_trace, updated={mutations.Updated}, deleted={mutations.Deleted}, inserted={mutations.Inserted}");
                Console.WriteLine(
                    $"compact, elapsed_ms={compactMs:F2}, result=REFERENCE");
                Console.WriteLine(
                    $"integrated_predicate_2hop, p50_ms={p50:F4}, p95_ms={p95:F4}, " +
                    $"required_p50_ms={RequiredReadPathP50Ms:F4}, result={(readPass ? "PASS" : "FAIL")}");
                Console.WriteLine(
                    "csv,csr_product_integration,payload_matches,row_matches,mismatches,compact_ms,p50_ms,p95_ms,result");
                Console.WriteLine(
                    $"csv,csr_product_integration,{validation.PayloadMatches},{validation.RowMatches}," +
                    $"{validation.Mismatches},{compactMs:F2},{p50:F4},{p95:F4},{(readPass ? "PASS" : "FAIL")}");

                return readPass ? 0 : 2;
            }
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    public static int RunMergeGate(IReadOnlyList<string> args)
    {
        int deltaCount = Parse(args, 0, 1_000_000);
        int batchSize = Parse(args, 1, 10_000);
        int targetPool = Parse(args, 2, 1_000);
        batchSize = Math.Min(batchSize, deltaCount);

        Console.WriteLine("=== Clean-slate CSR product merge gate ===");
        Console.WriteLine(
            $"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"delta_count={deltaCount}, batch_size={batchSize}, target_pool={targetPool}");
        Console.WriteLine();

        string dir = BenchTempDir.Create("clean_slate_csr_merge_gate");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "graph.quiver");
            var seeded = SeedMergeGateBase(path, targetPool);
            var ingest = AppendDeltaEdges(path, seeded, deltaCount, batchSize);

            double compactMs;
            using (var db = QuiverDatabase.Open(path))
            {
                var sw = Stopwatch.StartNew();
                db.CompactAdjacency();
                sw.Stop();
                compactMs = sw.Elapsed.TotalMilliseconds;
            }

            using var reopened = QuiverDatabase.Open(path);
            using var tx = reopened.BeginReadOnlyTransaction();
            var validation = ValidateMergeGate(tx, seeded.Hub, expectedEdges: deltaCount + 1);
            bool pass = validation.Passed && compactMs <= RequiredMergeGateMs;

            Console.WriteLine(
                $"delta_ingest, inserted={ingest.Inserted}, batches={ingest.Batches}, elapsed_ms={ingest.ElapsedMs:F2}, result=REFERENCE");
            Console.WriteLine(
                $"merge_gate, compact_ms={compactMs:F2}, required_ms={RequiredMergeGateMs:F2}, " +
                $"edges={validation.Edges}, payload_matches={validation.PayloadMatches}, " +
                $"result={(pass ? "PASS" : "FAIL")}");
            Console.WriteLine(
                "csv,csr_product_merge_gate,delta_count,batch_size,target_pool,ingest_ms,compact_ms,required_ms,edges,payload_matches,result");
            Console.WriteLine(
                $"csv,csr_product_merge_gate,{deltaCount},{batchSize},{targetPool},{ingest.ElapsedMs:F2},{compactMs:F2}," +
                $"{RequiredMergeGateMs:F2},{validation.Edges},{validation.PayloadMatches},{(pass ? "PASS" : "FAIL")}");

            return pass ? 0 : 2;
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

    private static Corpus BuildBulkGraph(string path, int degree)
    {
        using var db = QuiverDatabase.Open(path);
        var label = db.Schema.GetOrCreateLabel("V");
        var type = db.Schema.GetOrCreateEdgeType("LINK");
        var scoreKey = db.Schema.GetOrCreatePropertyKey(ScoreKey);
        var payload = PayloadLaneSpec.ForInt64(scoreKey.Value);

        int vertexCount = 1 + degree + degree * degree + degree;
        var scores = new Dictionary<long, long>();

        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        loader.WithPayloadLane(payload);
        for (int n = 0; n < vertexCount; n++)
            loader.AppendVertex(new VertexId(n), label);

        long edgeId = 0;
        long firstSecondHop = -1;
        for (int mid = 0; mid < degree; mid++)
        {
            long midVertex = 1 + mid;
            Append(loader, edgeId, 0, midVertex, type, scoreKey, 0, scores);
            edgeId++;

            for (int leaf = 0; leaf < degree; leaf++)
            {
                if (firstSecondHop < 0)
                    firstSecondHop = edgeId;
                long leafVertex = 1 + degree + (long)mid * degree + leaf;
                long score = (mid + leaf) % 4 == 0 ? 1 : 0;
                Append(loader, edgeId, midVertex, leafVertex, type, scoreKey, score, scores);
                edgeId++;
            }
        }

        loader.Commit();
        return new Corpus(new VertexId(0), degree, edgeId, firstSecondHop, scores);
    }

    private static MergeGateSeed SeedMergeGateBase(string path, int targetPool)
    {
        using var db = QuiverDatabase.Open(path);
        var label = db.Schema.GetOrCreateLabel("V");
        var type = db.Schema.GetOrCreateEdgeType("LINK");
        var scoreKey = db.Schema.GetOrCreatePropertyKey(ScoreKey);
        var payload = PayloadLaneSpec.ForInt64(scoreKey.Value);

        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        loader.WithPayloadLane(payload);
        loader.AppendVertex(new VertexId(0), label);
        for (int i = 1; i <= targetPool; i++)
            loader.AppendVertex(new VertexId(i), label);
        loader.AppendEdge(new EdgeId(0), new VertexId(0), new VertexId(1), type);
        loader.AppendEdgePayload(new EdgeId(0), scoreKey, 1);
        loader.Commit();

        using var tx = db.BeginTransaction();
        var value = PropertyValue.FromInt64(1);
        tx.SetProperty(new EdgeId(0), ScoreKey, in value);
        tx.Commit();

        return new MergeGateSeed(new VertexId(0), targetPool);
    }

    private static IngestSummary AppendDeltaEdges(
        string path,
        MergeGateSeed seed,
        int deltaCount,
        int batchSize)
    {
        var sw = Stopwatch.StartNew();
        int inserted = 0;
        int batches = 0;
        using var db = QuiverDatabase.Open(path);
        while (inserted < deltaCount)
        {
            using var tx = db.BeginTransaction();
            int take = Math.Min(batchSize, deltaCount - inserted);
            for (int i = 0; i < take; i++)
            {
                int ordinal = inserted + i;
                var target = new VertexId(1 + (ordinal % seed.TargetPool));
                var edge = tx.CreateEdge(seed.Hub, target, "LINK");
                var score = PropertyValue.FromInt64((ordinal & 1) == 0 ? 1 : 0);
                tx.SetProperty(edge, ScoreKey, in score);
            }

            tx.Commit();
            inserted += take;
            batches++;
        }

        sw.Stop();
        return new IngestSummary(inserted, batches, sw.Elapsed.TotalMilliseconds);
    }

    private static void Append(
        BulkLoader loader,
        long edgeId,
        long source,
        long target,
        EdgeTypeId type,
        PropertyKeyId scoreKey,
        long score,
        Dictionary<long, long> scores)
    {
        var id = new EdgeId(edgeId);
        loader.AppendEdge(id, new VertexId(source), new VertexId(target), type);
        loader.AppendEdgePayload(id, scoreKey, score);
        scores[edgeId] = score;
    }

    private static void SeedEdgeProperties(string path, Corpus corpus)
    {
        using var db = QuiverDatabase.Open(path);
        using var tx = db.BeginTransaction();
        foreach (var (edgeSeq, score) in corpus.Scores)
        {
            var value = PropertyValue.FromInt64(score);
            tx.SetProperty(new EdgeId(edgeSeq), ScoreKey, in value);
        }
        tx.Commit();
    }

    private static MutationSummary ApplyDeterministicMutations(string path, Corpus corpus, int mutationCount)
    {
        using var db = QuiverDatabase.Open(path);
        using var tx = db.BeginTransaction();

        int updated = 0;
        int deleted = 0;
        int inserted = 0;
        int limit = Math.Min(mutationCount, corpus.Degree);
        for (int i = 0; i < limit; i++)
        {
            long edgeSeq = corpus.FirstSecondHopEdge + i;
            var update = PropertyValue.FromInt64(i % 2 == 0 ? 1 : 0);
            tx.SetProperty(new EdgeId(edgeSeq), ScoreKey, in update);
            updated++;
        }

        for (int i = 0; i < limit; i++)
        {
            long edgeSeq = corpus.FirstSecondHopEdge + corpus.Degree + i;
            tx.DeleteEdge(new EdgeId(edgeSeq));
            deleted++;
        }

        for (int mid = 0; mid < limit; mid++)
        {
            var edge = tx.CreateEdge(
                new VertexId(1 + mid),
                new VertexId(1 + corpus.Degree + corpus.Degree * corpus.Degree + mid),
                "LINK");
            var score = PropertyValue.FromInt64(mid % 3 == 0 ? 1 : 0);
            tx.SetProperty(edge, ScoreKey, in score);
            inserted++;
        }

        tx.Commit();
        return new MutationSummary(updated, deleted, inserted);
    }

    private static ValidationResult ValidatePayloadAgainstRowPath(IGraphTransaction tx, VertexId hub)
    {
        int payloadMatches = 0;
        int rowMatches = 0;
        int mismatches = 0;

        var adj = tx.AsInternal().AdjacencyBlocks
            ?? throw new InvalidOperationException("Adjacency block store was not built.");

        using var first = adj.OpenCursor(hub, Direction.Outgoing, null);
        while (first.MoveNext())
        {
            if (adj.IsTombstoned(first.Edge))
                continue;

            using var second = adj.OpenCursor(first.Neighbor, Direction.Outgoing, null);
            while (second.MoveNext())
            {
                if (adj.IsTombstoned(second.Edge))
                    continue;

                long payloadScore = second.WeightRaw;
                var rowValue = tx.GetProperty(second.Edge, ScoreKey);
                long rowScore = rowValue.Type == PropertyValueType.Int64 ? rowValue.Int64Value : 0;
                if (payloadScore != rowScore)
                    mismatches++;
                if (payloadScore == 1)
                    payloadMatches++;
                if (rowScore == 1)
                    rowMatches++;
            }
        }

        return new ValidationResult(payloadMatches, rowMatches, mismatches);
    }

    private static int CountPayloadMatches(IGraphTransaction tx, VertexId hub)
    {
        var adj = tx.AsInternal().AdjacencyBlocks
            ?? throw new InvalidOperationException("Adjacency block store was not built.");

        int count = 0;
        using var first = adj.OpenCursor(hub, Direction.Outgoing, null);
        while (first.MoveNext())
        {
            if (adj.IsTombstoned(first.Edge))
                continue;

            using var second = adj.OpenCursor(first.Neighbor, Direction.Outgoing, null);
            while (second.MoveNext())
            {
                if (!adj.IsTombstoned(second.Edge) && second.WeightRaw == 1)
                    count++;
            }
        }

        return count;
    }

    private static MergeGateValidation ValidateMergeGate(
        IGraphTransaction tx,
        VertexId hub,
        int expectedEdges)
    {
        var adj = tx.AsInternal().AdjacencyBlocks
            ?? throw new InvalidOperationException("Adjacency block store was not built.");
        if (adj is not IAdjacencyPayloadView)
            throw new InvalidOperationException("Payload adjacency view was not built.");

        int edges = 0;
        int payloadMatches = 0;
        using var cursor = adj.OpenCursor(hub, Direction.Outgoing, null);
        while (cursor.MoveNext())
        {
            if (adj.IsTombstoned(cursor.Edge))
                continue;

            edges++;
            if (cursor.WeightRaw == 1)
                payloadMatches++;
        }

        return new MergeGateValidation(edges, payloadMatches, expectedEdges);
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

    private readonly record struct Corpus(
        VertexId Hub,
        int Degree,
        long EdgeCount,
        long FirstSecondHopEdge,
        Dictionary<long, long> Scores);

    private readonly record struct MutationSummary(int Updated, int Deleted, int Inserted);

    private readonly record struct ValidationResult(int PayloadMatches, int RowMatches, int Mismatches)
    {
        public bool Passed => PayloadMatches == RowMatches && Mismatches == 0;
    }

    private readonly record struct MergeGateSeed(VertexId Hub, int TargetPool);

    private readonly record struct IngestSummary(int Inserted, int Batches, double ElapsedMs);

    private readonly record struct MergeGateValidation(
        int Edges,
        int PayloadMatches,
        int ExpectedEdges)
    {
        public bool Passed => Edges == ExpectedEdges;
    }
}
