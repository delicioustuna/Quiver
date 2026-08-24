using System.Diagnostics;
using System.Runtime.InteropServices;
using Yatagarasu;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Testing;

namespace Yatagarasu.Benchmarks.Standalone;

/// <summary>
/// Clean-slate redesign の次段 spike に向けて、QUIVER-SW page-WAL カーネル上の比較値を
/// standalone 形式でまとめて取得するランナー。
/// </summary>
public static class CleanSlatePageWalBaselineRunner
{
    private const string EdgeType = "LINK";
    private const string EdgeScoreKey = "score";
    private const string FullTextIndex = "clean_slate_body";
    private const string VectorIndex = "clean_slate_vector";
    private const int FtsBatchSize = 200;
    private const int FtsAmpSampleChunks = 5_000;

    public static int Run(IReadOnlyList<string> args)
    {
        int degree = Parse(args, 0, 100);
        int traversalIterations = Parse(args, 1, 300);
        int fullTextChunks = Parse(args, 2, 100_000);
        int fullTextQueries = Parse(args, 3, 500);
        int vectorCount = Parse(args, 4, VectorRecallCorpus.RecallCount);
        int vectorQueries = Parse(args, 5, VectorRecallCorpus.QueryCount);

        Console.WriteLine("=== Clean-slate QUIVER-SW page-WAL baseline ===");
        Console.WriteLine(
            $"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine(
            $"degree={degree}, traversalIterations={traversalIterations}, " +
            $"fullTextChunks={fullTextChunks}, fullTextQueries={fullTextQueries}, " +
            $"vectorCount={vectorCount}, vectorQueries={vectorQueries}");
        Console.WriteLine();

        MeasureEdgeBaseline(degree, traversalIterations);
        Console.WriteLine();
        MeasureFullTextBaseline(fullTextChunks, fullTextQueries);
        Console.WriteLine();
        MeasureVectorBaseline(vectorCount, vectorQueries);

        return 0;
    }

    private static int Parse(IReadOnlyList<string> args, int index, int fallback)
        => index < args.Count && int.TryParse(args[index], out int value) && value > 0
            ? value
            : fallback;

    private static void MeasureEdgeBaseline(int degree, int traversalIterations)
    {
        Console.WriteLine("--- edge row-path baseline ---");
        string dir = BenchTempDir.Create("clean_slate_edge");
        try
        {
            using var db = YatagarasuDatabase.Open(Path.Combine(dir, "graph.yata"));
            var shape = BuildEdgeCorpus(db, degree);

            double traversalP50Ms;
            double traversalP95Ms;
            double directLookupNs;
            using (var read = db.BeginReadTransaction())
            {
                int warmup = CountPredicateMatches(read, shape.Hub);
                GC.KeepAlive(warmup);

                var traversalTicks = new long[traversalIterations];
                for (int i = 0; i < traversalTicks.Length; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    int count = CountPredicateMatches(read, shape.Hub);
                    traversalTicks[i] = Stopwatch.GetTimestamp() - started;
                    if (count != shape.ExpectedMatches)
                        throw new InvalidOperationException(
                            $"Unexpected predicate match count. Expected {shape.ExpectedMatches}, got {count}.");
                }

                traversalP50Ms = PercentileMs(traversalTicks, 0.50);
                traversalP95Ms = PercentileMs(traversalTicks, 0.95);
                directLookupNs = MeasureDirectEdgeLookup(read, shape.SecondHopEdges);
                Console.WriteLine(
                    $"predicate_2hop, edges={shape.SecondHopEdges.Length}, " +
                    $"matches={shape.ExpectedMatches}, p50_ms={traversalP50Ms:F4}, " +
                    $"p95_ms={traversalP95Ms:F4}, " +
                    $"throughput_traversals_per_sec={1000.0 / traversalP50Ms:F1}");
                Console.WriteLine($"edge_property_direct_lookup, ns_per_lookup={directLookupNs:F1}");
            }

            var update = MeasurePointUpdateCommits(
                db,
                shape.SecondHopEdges,
                Math.Max(200, traversalIterations));
            Console.WriteLine(
                $"edge_property_update_commit, commits={update.Count}, " +
                $"p50_us={update.P50Us:F2}, p95_us={update.P95Us:F2}");
            Console.WriteLine(
                "csv,edge,predicate_2hop_p50_ms,predicate_2hop_p95_ms," +
                "direct_lookup_ns,update_commit_p50_us,update_commit_p95_us");
            Console.WriteLine(
                $"csv,edge,{traversalP50Ms:F4},{traversalP95Ms:F4}," +
                $"{directLookupNs:F1},{update.P50Us:F2},{update.P95Us:F2}");
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static EdgeCorpus BuildEdgeCorpus(YatagarasuDatabase db, int degree)
    {
        var secondHopEdges = new EdgeId[degree * degree];
        int expectedMatches = 0;

        using var tx = db.BeginWriteTransaction();
        var hub = tx.CreateVertex("Hub");
        int relIndex = 0;
        for (int i = 0; i < degree; i++)
        {
            var mid = tx.CreateVertex("Mid");
            tx.CreateEdge(hub, mid, EdgeType);
            for (int j = 0; j < degree; j++)
            {
                var leaf = tx.CreateVertex("Leaf");
                var edge = tx.CreateEdge(mid, leaf, EdgeType);
                long score = (i + j) % 4 == 0 ? 1 : 0;
                tx.SetProperty(edge, EdgeScoreKey, PropertyValue.FromInt64(score));
                secondHopEdges[relIndex++] = edge;
                if (score == 1) expectedMatches++;
            }
        }
        tx.Commit();

        return new EdgeCorpus(hub, secondHopEdges, expectedMatches);
    }

    private static int CountPredicateMatches(IReadTransaction tx, VertexId hub)
    {
        int count = 0;
        var first = tx.EnumerateEdges(hub, Direction.Outgoing, EdgeType);
        while (first.MoveNext())
        {
            var mid = first.Current.Target;
            var second = tx.EnumerateEdges(mid, Direction.Outgoing, EdgeType);
            while (second.MoveNext())
            {
                var value = tx.GetProperty(second.Current.Id, EdgeScoreKey);
                if (value.Int64Value == 1)
                    count++;
            }
            second.Dispose();
        }
        first.Dispose();
        return count;
    }

    private static double MeasureDirectEdgeLookup(
        IReadTransaction tx,
        IReadOnlyList<EdgeId> edgeIds)
    {
        const int SampleSize = 4096;
        int[] sample = new int[SampleSize];
        var rng = new Random(1234);
        for (int i = 0; i < sample.Length; i++)
            sample[i] = rng.Next(edgeIds.Count);

        int cursor = 0;
        long sum = 0;
        long Lookup()
        {
            var edge = edgeIds[sample[cursor++ & (SampleSize - 1)]];
            sum += tx.GetProperty(edge, EdgeScoreKey).Int64Value;
            return sum;
        }

        return TimePerOpNs(Lookup, 100_000);
    }

    private static PointUpdateResult MeasurePointUpdateCommits(
        YatagarasuDatabase db,
        IReadOnlyList<EdgeId> edgeIds,
        int commits)
    {
        var updateTicks = new long[commits];
        for (int i = 0; i < commits; i++)
        {
            var edge = edgeIds[i % edgeIds.Count];
            long started = Stopwatch.GetTimestamp();
            using (var tx = db.BeginWriteTransaction())
            {
                tx.SetProperty(edge, EdgeScoreKey, PropertyValue.FromInt64(10_000 + i));
                tx.Commit();
            }
            updateTicks[i] = Stopwatch.GetTimestamp() - started;
        }

        Array.Sort(updateTicks);
        return new PointUpdateResult(
            commits,
            PercentileUs(updateTicks, 0.50),
            PercentileUs(updateTicks, 0.95));
    }

    private static void MeasureFullTextBaseline(int chunkCount, int queryCount)
    {
        Console.WriteLine("--- full-text page-WAL baseline ---");
        var vocab = new ZipfVocabulary(Math.Clamp(chunkCount / 2, 4_000, 60_000));
        int ampChunks = Math.Min(chunkCount, FtsAmpSampleChunks);
        long walWithFullText = MeasureFullTextWal(vocab, ampChunks, withIndex: true, out double ingestMsFullText);
        long walPlain = MeasureFullTextWal(vocab, ampChunks, withIndex: false, out double ingestMsPlain);
        double amplification = walPlain > 0 ? walWithFullText / (double)walPlain : double.NaN;

        Console.WriteLine(
            $"fulltext_ingest_wal, chunks={ampChunks}, with_index_bytes={walWithFullText}, " +
            $"plain_bytes={walPlain}, amplification={amplification:F2}, " +
            $"with_index_ms={ingestMsFullText:F0}, plain_ms={ingestMsPlain:F0}");

        string dir = BenchTempDir.Create("clean_slate_fts");
        try
        {
            using var db = YatagarasuDatabase.Open(Path.Combine(dir, "graph.yata"));
            db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(FullTextIndex, new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
            double ingestMs = IngestFullTextCorpus(db, vocab, chunkCount, seed: 11);
            var latencies = MeasureFullTextSearchLatencies(db, vocab, queryCount, seed: 99);
            Console.WriteLine(
                $"fulltext_search, chunks={chunkCount}, queries={queryCount}, " +
                $"build_ms={ingestMs:F0}, p50_ms={Percentile(latencies, 0.50):F3}, " +
                $"p95_ms={Percentile(latencies, 0.95):F3}, max_ms={latencies[^1]:F3}");
            Console.WriteLine(
                "csv,fulltext,chunks,queries,wal_amplification,search_p50_ms,search_p95_ms");
            Console.WriteLine(
                $"csv,fulltext,{chunkCount},{queryCount},{amplification:F2}," +
                $"{Percentile(latencies, 0.50):F3},{Percentile(latencies, 0.95):F3}");
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static long MeasureFullTextWal(
        ZipfVocabulary vocab,
        int chunkCount,
        bool withIndex,
        out double ingestMs)
    {
        string dir = BenchTempDir.Create(withIndex ? "clean_slate_fts_wal" : "clean_slate_plain_wal");
        try
        {
            using var db = YatagarasuDatabase.Open(
                Path.Combine(dir, "graph.yata"),
                new YatagarasuDatabaseOptions { CheckpointThresholdBytes = long.MaxValue });
            if (withIndex)
                db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(FullTextIndex, new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));

            ingestMs = IngestFullTextCorpus(db, vocab, chunkCount, seed: 11);
            return WalBytes(dir);
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static double IngestFullTextCorpus(
        YatagarasuDatabase db,
        ZipfVocabulary vocab,
        int chunkCount,
        int seed)
    {
        var rng = new Random(seed);
        var sw = Stopwatch.StartNew();
        int written = 0;
        while (written < chunkCount)
        {
            int batch = Math.Min(FtsBatchSize, chunkCount - written);
            using var tx = db.BeginWriteTransaction();
            for (int i = 0; i < batch; i++)
            {
                var vertex = tx.CreateVertex("Doc");
                tx.SetProperty(vertex, "body", PropertyValue.FromString(MakeChunk(vocab, rng)));
            }
            tx.Commit();
            written += batch;
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static List<double> MeasureFullTextSearchLatencies(
        YatagarasuDatabase db,
        ZipfVocabulary vocab,
        int queryCount,
        int seed)
    {
        var rng = new Random(seed);
        var stats = db.CollectStats();
        using var read = db.BeginReadTransaction();
        var g = read.Query.WithStats(stats);

        for (int i = 0; i < Math.Min(20, queryCount); i++)
            _ = g.Search(FullTextIndex, MakeQuery(vocab, rng, 2, 5), k: 20).ToList();

        var latencies = new List<double>(queryCount);
        for (int i = 0; i < queryCount; i++)
        {
            string query = MakeQuery(vocab, rng, 2, 5);
            var sw = Stopwatch.StartNew();
            var hits = g.Search(FullTextIndex, query, k: 20).ToList();
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            GC.KeepAlive(hits);
        }
        latencies.Sort();
        return latencies;
    }

    private static void MeasureVectorBaseline(int vectorCount, int queryCount)
    {
        Console.WriteLine("--- vector page-WAL baseline ---");
        string dir = BenchTempDir.Create("clean_slate_vector");
        try
        {
            var random = new Random(VectorRecallCorpus.Seed);
            var corpus = new List<float[]>(vectorCount);
            using var db = YatagarasuDatabase.Open(Path.Combine(dir, "graph.yata"));
            db.EditSchema(schema =>
            {
                schema.GetOrCreatePropertyKey("embedding");
                schema.CreateIndex(new VectorIndexDefinition(
                    VectorIndex,
                    new PropertyTarget(PropertyOwnerKind.Vertex, "embedding", "Doc"),
                    VectorRecallCorpus.RecallDimensions));
            });

            var build = Stopwatch.StartNew();
            using (var tx = db.BeginWriteTransaction())
            {
                for (int i = 0; i < vectorCount; i++)
                {
                    var vector = VectorRecallCorpus.NextVector(random, VectorRecallCorpus.RecallDimensions);
                    corpus.Add(vector);
                    var vertex = tx.CreateVertex("Doc");
                    tx.SetVectorProperty(EntityRef.From(vertex), "embedding", vector);
                }
                tx.Commit();
            }
            build.Stop();

            var queries = Enumerable.Range(0, queryCount)
                .Select(_ => VectorRecallCorpus.NextVector(random, VectorRecallCorpus.RecallDimensions))
                .ToArray();
            var result = MeasureVectorRecallAndLatency(db, corpus, queries);
            Console.WriteLine(
                $"vector_search, vectors={vectorCount}, queries={queryCount}, " +
                $"build_ms={build.Elapsed.TotalMilliseconds:F0}, " +
                $"recall@{VectorRecallCorpus.K}={result.Recall:F3}, " +
                $"mean_ms={result.MeanLatencyMs:F3}, p50_ms={result.P50LatencyMs:F3}");
            Console.WriteLine("csv,vector,vectors,queries,recall,mean_ms,p50_ms");
            Console.WriteLine(
                $"csv,vector,{vectorCount},{queryCount},{result.Recall:F3}," +
                $"{result.MeanLatencyMs:F3},{result.P50LatencyMs:F3}");
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static (double Recall, double MeanLatencyMs, double P50LatencyMs) MeasureVectorRecallAndLatency(
        YatagarasuDatabase db,
        IReadOnlyList<float[]> corpus,
        IReadOnlyList<float[]> queries)
    {
        using (var warmupTx = db.BeginReadTransaction())
        using (var warmup = warmupTx.KnnSearch(VectorIndex, queries[0], VectorRecallCorpus.K))
            while (warmup.MoveNext()) { }

        double recallTotal = 0;
        var latencies = new List<double>(queries.Count);
        foreach (var query in queries)
        {
            var approximate = new List<long>(VectorRecallCorpus.K);
            var sw = Stopwatch.StartNew();
            using (var tx = db.BeginReadTransaction())
            using (var cursor = tx.KnnSearch(VectorIndex, query, VectorRecallCorpus.K))
            {
                while (cursor.MoveNext())
                    approximate.Add(cursor.Current.Owner.Sequence);
            }
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);

            var exact = Enumerable.Range(0, corpus.Count)
                .Select(i => (Seq: (long)i, Score: Cosine(query, corpus[i])))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Seq)
                .Take(VectorRecallCorpus.K)
                .Select(x => x.Seq)
                .ToHashSet();
            recallTotal += approximate.Count(exact.Contains) / (double)VectorRecallCorpus.K;
        }

        latencies.Sort();
        return (
            recallTotal / queries.Count,
            latencies.Sum() / latencies.Count,
            Percentile(latencies, 0.50));
    }

    private static float Cosine(float[] left, float[] right)
    {
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (int i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }
        double denominator = Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm);
        return denominator == 0 ? 0 : (float)(dot / denominator);
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

    private static double PercentileMs(long[] sortedOrUnsortedTicks, double p)
        => PercentileTicks(sortedOrUnsortedTicks, p) * 1000.0 / Stopwatch.Frequency;

    private static double PercentileUs(long[] sortedOrUnsortedTicks, double p)
        => PercentileTicks(sortedOrUnsortedTicks, p) * 1_000_000.0 / Stopwatch.Frequency;

    private static long PercentileTicks(long[] ticks, double p)
    {
        var sorted = (long[])ticks.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static double Percentile(List<double> sorted, double p)
    {
        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static string MakeChunk(ZipfVocabulary vocab, Random rng)
    {
        int len = rng.Next(60, 120);
        var builder = new System.Text.StringBuilder(len * 8);
        for (int i = 0; i < len; i++)
        {
            if (i > 0)
                builder.Append(' ');
            builder.Append(vocab.Sample(rng));
        }
        return builder.ToString();
    }

    private static string MakeQuery(ZipfVocabulary vocab, Random rng, int minTerms, int maxTerms)
    {
        int terms = rng.Next(minTerms, maxTerms + 1);
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < terms; i++)
        {
            if (i > 0)
                builder.Append(' ');
            builder.Append(vocab.Sample(rng));
        }
        return builder.ToString();
    }

    private static long WalBytes(string dir)
    {
        string wal = Path.Combine(dir, "graph.yata-wal");
        return File.Exists(wal) ? new FileInfo(wal).Length : 0;
    }

    private readonly record struct EdgeCorpus(
        VertexId Hub,
        EdgeId[] SecondHopEdges,
        int ExpectedMatches);

    private readonly record struct PointUpdateResult(
        int Count,
        double P50Us,
        double P95Us);

    private sealed class ZipfVocabulary
    {
        private readonly string[] _terms;
        private readonly double[] _cdf;

        public ZipfVocabulary(int size)
        {
            _terms = new string[size];
            _cdf = new double[size];
            double sum = 0;
            for (int i = 0; i < size; i++)
            {
                _terms[i] = "term" + i.ToString("D5");
                sum += 1.0 / (i + 1);
                _cdf[i] = sum;
            }

            for (int i = 0; i < size; i++)
                _cdf[i] /= sum;
        }

        public string Sample(Random rng)
        {
            double value = rng.NextDouble();
            int lo = 0;
            int hi = _cdf.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_cdf[mid] < value)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return _terms[lo];
        }
    }
}
