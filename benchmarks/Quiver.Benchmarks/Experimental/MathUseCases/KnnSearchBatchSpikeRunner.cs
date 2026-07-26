using System.Diagnostics;
using Quiver.Api;
using Quiver.Core;

namespace Quiver.Benchmarks.Experimental.MathUseCases;

internal static class KnnSearchBatchSpikeRunner
{
    private const int CorpusSize = 250;
    private const int QueryCount = 32;
    private const int Dimensions = 384;
    private const int K = 10;
    private const string IndexName = "batch_spike_vectors";
    private const string PropertyName = "embedding";

    internal static int Run()
    {
        string directory = BenchTempDir.Create("knn_batch_spike");
        string path = Path.Combine(directory, "graph.quiver");
        try
        {
            using var database = QuiverDatabase.Open(path);
            database.EditSchema(schema =>
            {
                schema.GetOrCreatePropertyKey(PropertyName);
                schema.CreateIndex(new VectorIndexDefinition(
                    IndexName,
                    new PropertyTarget(PropertyOwnerKind.Vertex, PropertyName, "Point"),
                    Dimensions,
                    DistanceMetric.Euclidean));
            });

            (float[][] corpus, float[][] queries) = CreateData();
            var owners = new EntityRef[CorpusSize];
            using (IWriteTransaction write = database.BeginWriteTransaction())
            {
                for (int i = 0; i < corpus.Length; i++)
                {
                    owners[i] = EntityRef.From(write.CreateVertex("Point"));
                    write.SetVectorProperty(owners[i], PropertyName, corpus[i]);
                }
                write.Commit();
            }

            using IReadTransaction read = database.BeginReadTransaction();
            Measurement repeated = Measure(() => RepeatedSearches(read, queries));
            Measurement batch = Measure(() => CurrentBatch(read, queries));
            ValidateEqual(repeated.Results, batch.Results);

            double speedup = repeated.Milliseconds / batch.Milliseconds;
            double allocationReduction = 1 - batch.AllocatedBytes / (double)repeated.AllocatedBytes;
            Console.WriteLine(
                $"knn-batch-spike corpus={CorpusSize} queries={QueryCount} dimension={Dimensions} k={K} " +
                $"repeatedMs={repeated.Milliseconds:F3} repeatedAlloc={repeated.AllocatedBytes} " +
                $"batchMs={batch.Milliseconds:F3} batchAlloc={batch.AllocatedBytes} " +
                $"speedup={speedup:F2} allocationReductionPct={allocationReduction * 100:F2} " +
                "resultsMatch=true samples=3 warmup=1");
            return 0;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static Measurement Measure(Func<BatchHit[][]> action)
    {
        _ = action();
        var samples = new Measurement[3];
        for (int i = 0; i < samples.Length; i++)
        {
            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            BatchHit[][] results = action();
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            samples[i] = new Measurement(milliseconds, allocatedBytes, results);
        }
        return samples.OrderBy(static sample => sample.Milliseconds).ElementAt(1);
    }

    private static BatchHit[][] CurrentBatch(
        IReadTransaction read,
        IReadOnlyList<float[]> queries)
    {
        ReadOnlyMemory<float>[] queryMemory = queries
            .Select(static query => new ReadOnlyMemory<float>(query))
            .ToArray();
        IReadOnlyList<VectorSearchCursor> cursors = read.KnnSearchBatch(
            IndexName,
            queryMemory,
            K);
        var results = new BatchHit[cursors.Count][];
        try
        {
            for (int query = 0; query < cursors.Count; query++)
            {
                var hits = new List<BatchHit>(K);
                while (cursors[query].MoveNext())
                {
                    VectorSearchResult hit = cursors[query].Current;
                    hits.Add(new BatchHit(hit.Owner, hit.Score));
                }
                results[query] = hits.ToArray();
            }
        }
        finally
        {
            foreach (VectorSearchCursor cursor in cursors)
                cursor.Dispose();
        }
        return results;
    }

    private static BatchHit[][] RepeatedSearches(
        IReadTransaction read,
        IReadOnlyList<float[]> queries)
    {
        var results = new BatchHit[queries.Count][];
        for (int query = 0; query < queries.Count; query++)
        {
            using VectorSearchCursor cursor = read.KnnSearch(IndexName, queries[query], K);
            var hits = new List<BatchHit>(K);
            while (cursor.MoveNext())
            {
                VectorSearchResult hit = cursor.Current;
                hits.Add(new BatchHit(hit.Owner, hit.Score));
            }
            results[query] = hits.ToArray();
        }
        return results;
    }

    private static void ValidateEqual(BatchHit[][] expected, BatchHit[][] actual)
    {
        if (expected.Length != actual.Length)
            throw new InvalidOperationException("query count differs");
        for (int query = 0; query < expected.Length; query++)
        {
            if (!expected[query].AsSpan().SequenceEqual(actual[query]))
                throw new InvalidOperationException($"batch result differs at query {query}");
        }
    }

    private static (float[][] Corpus, float[][] Queries) CreateData()
    {
        var random = new Random(0x42415443);
        var corpus = new float[CorpusSize][];
        for (int i = 0; i < corpus.Length; i++)
            corpus[i] = CreateVector(random, i % 5);
        var queries = new float[QueryCount][];
        for (int i = 0; i < queries.Length; i++)
            queries[i] = CreateVector(random, i % 5);
        return (corpus, queries);
    }

    private static float[] CreateVector(Random random, int cluster)
    {
        var vector = new float[Dimensions];
        vector[0] = cluster * 20;
        vector[1] = (cluster & 1) * 10;
        for (int i = 0; i < vector.Length; i++)
            vector[i] += (float)(random.NextDouble() * 0.2 - 0.1);
        return vector;
    }

    private readonly record struct BatchHit(EntityRef Owner, float Score);

    private readonly record struct Measurement(
        double Milliseconds,
        long AllocatedBytes,
        BatchHit[][] Results);
}
