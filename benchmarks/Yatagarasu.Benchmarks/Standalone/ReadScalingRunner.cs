using System.Collections.Concurrent;
using System.Diagnostics;
using Yatagarasu;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Testing;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks.Standalone;

/// <summary>
/// read-only 1-hop / KNN / BM25 の 1, 2, 4, 8 thread scaling を同一 DB、
/// 同一固定時間で測る。並行読み取り改善の効果測定の基準となる実測分母。
/// </summary>
public static class ReadScalingRunner
{
    private const string VectorIndex = "read_scaling_vectors";
    private const string FullTextIndex = "read_scaling_body";
    private const int Dimensions = VectorRecallCorpus.RecallDimensions;
    private const int CorpusCount = 2_000;
    private const int Degree = 128;
    private const int DurationMilliseconds = 1_000;

    public static int Run()
    {
        Console.WriteLine("=== Concurrent read scaling ===");
        Console.WriteLine($"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
                          $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine("workload, threads, ops/sec, speedup_vs_1t, linear_efficiency");

        string dir = BenchTempDir.Create("read_scaling");
        try
        {
            using var db = YatagarasuDatabase.Open(Path.Combine(dir, "graph.yata"));
            var (hub, query) = Seed(db);

            MeasureWorkload("1-hop", thread => CreateOneHopWorker(db, hub));
            MeasureWorkload("KNN", thread => CreateKnnWorker(db, query));
            MeasureWorkload("BM25", thread => CreateBm25Worker(db));
            return 0;
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static (VertexId Hub, float[] Query) Seed(YatagarasuDatabase db)
    {
        db.EditSchema(schema =>
        {
            schema.CreateIndex(new FullTextIndexDefinition(FullTextIndex, new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc")));
            schema.GetOrCreatePropertyKey("embedding");
            schema.CreateIndex(new VectorIndexDefinition(
                VectorIndex,
                new PropertyTarget(PropertyOwnerKind.Vertex, "embedding", "Doc"),
                Dimensions));
        });

        var random = new Random(VectorRecallCorpus.Seed);
        var vector = new float[Dimensions];
        VertexId hub;
        using (var tx = db.BeginWriteTransaction())
        {
            hub = tx.CreateVertex("Hub");
            for (int i = 0; i < Degree; i++)
            {
                var neighbor = tx.CreateVertex("Neighbor");
                tx.CreateEdge(hub, neighbor, "LINK");
            }

            for (int i = 0; i < CorpusCount; i++)
            {
                var vertex = tx.CreateVertex("Doc");
                tx.SetProperty(vertex, "body",
                    PropertyValue.FromString($"alpha beta corpus token{i % 64}"));
                VectorRecallCorpus.Fill(random, vector);
                tx.SetVectorProperty(EntityRef.From(vertex), "embedding", vector);
            }
            tx.Commit();
        }

        var query = new float[Dimensions];
        VectorRecallCorpus.Fill(random, query);
        return (hub, query);
    }

    private static Worker CreateOneHopWorker(YatagarasuDatabase db, VertexId hub)
    {
        var tx = db.BeginReadTransaction();
        return new Worker(() =>
        {
            int count = 0;
            var edges = tx.EnumerateEdges(hub, Direction.Outgoing);
            while (edges.MoveNext()) count++;
            return count;
        }, tx);
    }

    private static Worker CreateKnnWorker(YatagarasuDatabase db, float[] query)
    {
        var tx = db.BeginReadTransaction();
        return new Worker(() =>
        {
            int count = 0;
            using var cursor = tx.KnnSearch(VectorIndex, query, VectorRecallCorpus.K);
            while (cursor.MoveNext()) count++;
            return count;
        }, tx);
    }

    private static Worker CreateBm25Worker(YatagarasuDatabase db)
    {
        var tx = db.BeginReadTransaction();
        return new Worker(
            () => tx.Query.Search(FullTextIndex, "alpha beta", 10).ToList().Count,
            tx);
    }

    private static void MeasureWorkload(string name, Func<int, Worker> createWorker)
    {
        // ページ、JIT、tokenizer を 1 回温めてから scaling curve を取る。
        using (var warmup = createWorker(0))
            for (int i = 0; i < 20; i++) _ = warmup.Invoke();

        double baseline = 0;
        foreach (int threadCount in new[] { 1, 2, 4, 8 })
        {
            double opsPerSecond = MeasureConcurrent(threadCount, createWorker);
            if (threadCount == 1) baseline = opsPerSecond;
            double speedup = opsPerSecond / baseline;
            Console.WriteLine(
                $"{name}, {threadCount}, {opsPerSecond:F0}, {speedup:F2}, {speedup / threadCount:P1}");
        }
    }

    private static double MeasureConcurrent(int threadCount, Func<int, Worker> createWorker)
    {
        using var ready = new CountdownEvent(threadCount);
        using var start = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        var operations = new long[threadCount];
        var checksums = new long[threadCount];
        var threads = new Thread[threadCount];
        long endTimestamp = 0;

        for (int i = 0; i < threadCount; i++)
        {
            int local = i;
            threads[i] = new Thread(() =>
            {
                bool signaled = false;
                try
                {
                    using var worker = createWorker(local);
                    ready.Signal();
                    signaled = true;
                    start.Wait();
                    long count = 0;
                    long checksum = 0;
                    while (Stopwatch.GetTimestamp() < Volatile.Read(ref endTimestamp))
                    {
                        checksum += worker.Invoke();
                        count++;
                    }
                    operations[local] = count;
                    checksums[local] = checksum;
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    if (!signaled) ready.Signal();
                }
            })
            {
                IsBackground = true,
                Name = $"yatagarasu-read-scaling-{local}",
            };
            threads[i].Start();
        }

        ready.Wait();
        long started = Stopwatch.GetTimestamp();
        endTimestamp = started +
            (long)(DurationMilliseconds / 1000.0 * Stopwatch.Frequency);
        start.Set();
        foreach (var thread in threads) thread.Join();

        if (!errors.IsEmpty) throw new AggregateException(errors);
        long totalOperations = operations.Sum();
        long checksumTotal = checksums.Sum();
        if (checksumTotal < 0) Console.Write(string.Empty);
        double seconds = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
        return totalOperations / seconds;
    }

    private sealed class Worker(Func<int> operation, IDisposable? scope = null) : IDisposable
    {
        public int Invoke() => operation();
        public void Dispose() => scope?.Dispose();
    }
}
