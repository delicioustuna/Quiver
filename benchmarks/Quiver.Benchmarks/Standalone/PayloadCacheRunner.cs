using System.Diagnostics;
using Quiver.Core;
using Quiver.Testing;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// 同一の persistent HNSW を payload cache 無効 / 有効で開き直し、
/// page pin 除去だけの寄与を測る standalone spike。
/// </summary>
public static class PayloadCacheRunner
{
    private const string IndexName = "payload_cache_vectors";
    private const int K = 10;
    private const long CacheBudgetBytes = 64L * 1024 * 1024;

    public static int Run(int count, int dimensions, int queryCount)
    {
        if (count <= 0 || dimensions <= 0 || queryCount <= 0) return 2;
        Console.WriteLine("=== payload slab cache spike ===");
        Console.WriteLine($"N={count}, dim={dimensions}, k={K}, queries={queryCount}, " +
                          $"cache={CacheBudgetBytes / 1024 / 1024} MiB");

        string dir = BenchTempDir.Create("payload_cache");
        string path = Path.Combine(dir, "graph.quiver");
        try
        {
            float[] query = Seed(path, count, dimensions);
            var uncached = Measure(path, query, queryCount, cacheBudgetBytes: 0);
            var cached = Measure(path, query, queryCount, CacheBudgetBytes);

            bool sameResults = uncached.Ids.SequenceEqual(cached.Ids);
            double speedup = uncached.MeanMilliseconds / cached.MeanMilliseconds;
            double pinShare = 1.0 - cached.MeanMilliseconds / uncached.MeanMilliseconds;
            Console.WriteLine("mode, cold_ms, warm_mean_ms, ops_per_sec");
            Console.WriteLine(
                $"page-pin, {uncached.ColdMilliseconds:F3}, {uncached.MeanMilliseconds:F3}, " +
                $"{1000.0 / uncached.MeanMilliseconds:F0}");
            Console.WriteLine(
                $"slab-cache, {cached.ColdMilliseconds:F3}, {cached.MeanMilliseconds:F3}, " +
                $"{1000.0 / cached.MeanMilliseconds:F0}");
            Console.WriteLine($"speedup={speedup:F2}x, payload-pin attributable share={pinShare:P1}, " +
                              $"results-identical={sameResults}");
            return sameResults ? 0 : 1;
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static float[] Seed(string path, int count, int dimensions)
    {
        var options = new GraphDatabaseOptions { VectorCacheBudgetBytes = CacheBudgetBytes };
        using var db = GraphDatabase.Open(path, options);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName,
            EntityKind.Node,
            db.Schema.GetOrCreatePropertyKey("embedding"),
            dimensions,
            DistanceMetric.Cosine,
            "deterministic payload cache corpus"));

        var random = new Random(VectorRecallCorpus.Seed);
        var vector = new float[dimensions];
        const int batchSize = 1_000;
        for (int start = 0; start < count; start += batchSize)
        {
            using var tx = db.BeginTransaction();
            int end = Math.Min(count, start + batchSize);
            for (int i = start; i < end; i++)
            {
                var node = tx.CreateNode("Doc");
                VectorRecallCorpus.Fill(random, vector);
                tx.SetVector(EntityKind.Node, node.Value, IndexName, vector);
            }
            tx.Commit();
        }

        var query = new float[dimensions];
        VectorRecallCorpus.Fill(random, query);
        return query;
    }

    private static Measurement Measure(
        string path,
        float[] query,
        int queryCount,
        long cacheBudgetBytes)
    {
        using var db = GraphDatabase.Open(
            path,
            new GraphDatabaseOptions { VectorCacheBudgetBytes = cacheBudgetBytes });

        var sw = Stopwatch.StartNew();
        long[] ids = Search(db, query);
        sw.Stop();
        double cold = sw.Elapsed.TotalMilliseconds;

        // JIT と slab working set を温めた後の steady-state を測る。
        for (int i = 0; i < 5; i++) _ = Search(db, query);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        sw.Restart();
        long checksum = 0;
        for (int i = 0; i < queryCount; i++)
        {
            var result = Search(db, query);
            checksum += result.Length == 0 ? 0 : result[0];
        }
        sw.Stop();
        GC.KeepAlive(checksum);
        return new Measurement(cold, sw.Elapsed.TotalMilliseconds / queryCount, ids);
    }

    private static long[] Search(GraphDatabase db, float[] query)
    {
        var result = new List<long>(K);
        using var cursor = db.Vectors.KnnSearch(IndexName, query, K);
        while (cursor.MoveNext()) result.Add(cursor.Current.EntityId);
        return result.ToArray();
    }

    private sealed record Measurement(
        double ColdMilliseconds,
        double MeanMilliseconds,
        long[] Ids);
}
