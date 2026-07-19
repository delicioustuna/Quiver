using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Standalone;

/// <summary>product全文segmentのfan-out、merge、WAL、publish時間を測る。</summary>
public static class FullTextSegmentPublishRunner
{
    private const string IndexName = "product_fulltext_segment";
    private const double SearchGateMs = 8.55;
    private const double WalAmplificationGate = 11.74;
    private const double PublishGateMs = 500;

    public static int Run(IReadOnlyList<string> args)
    {
        int documentCount = Parse(args, 0, 20_000);
        int queryCount = Parse(args, 1, 300);
        Console.WriteLine("=== Product full-text segment gate ===");
        Console.WriteLine(
            $"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}, documents={documentCount}, queries={queryCount}");

        WriteAmplification amplification = MeasureWriteAmplification(
            Math.Min(documentCount, 5_000));
        ProductResult product = MeasureProductSegments(documentCount, queryCount);
        bool pass = product.SearchP50Ms <= SearchGateMs
            && amplification.Wal <= WalAmplificationGate
            && amplification.Total <= WalAmplificationGate
            && product.StrictEquivalent
            && product.MergeEquivalent
            && product.ReopenNoPrimaryScan
            && product.PublishP99Ms <= PublishGateMs;
        Console.WriteLine(
            $"fulltext_segment_product_result, result={(pass ? "PASS" : "FAIL")}, " +
            $"search_p50_ms={product.SearchP50Ms:F3}, wal_amplification={amplification.Wal:F2}, " +
            $"total_write_amplification={amplification.Total:F2}, reopen_primary_scans={product.ReopenPrimaryScans}, " +
            $"publish_p99_ms={product.PublishP99Ms:F3}");
        return pass ? 0 : 2;
    }

    private static ProductResult MeasureProductSegments(
        int documentCount,
        int queryCount)
    {
        string dir = BenchTempDir.Create("product_fulltext_segment");
        var publishDurations = new ConcurrentQueue<TimeSpan>();
        BinaryGraphStorageBackend.FullTextSegmentPublishMeasuredForTest =
            publishDurations.Enqueue;
        try
        {
            using var database = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));
            database.EditSchema(schema => schema.CreateIndex(
                new FullTextIndexDefinition(
                    IndexName,
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        "body",
                        "Doc"),
                    SegmentPolicy: new(
                        MaximumDeltaEntries: int.MaxValue,
                        MaximumSegments: 4,
                        MaximumTombstoneRatio: 1))));

            using (var initialize = database.BeginReadTransaction())
                _ = initialize.Query.Search(IndexName, "warmup", 10).ToList();
            var backend = (BinaryGraphStorageBackend)database.BackendInternal;
            backend.WaitForFullTextSegmentMergeForTest();

            int written = 0;
            for (int segment = 0; segment < 4; segment++)
            {
                int target = (int)((long)documentCount * (segment + 1) / 4);
                using var write = database.BeginWriteTransaction();
                while (written < target)
                {
                    VertexId document = write.CreateVertex("Doc");
                    write.SetProperty(
                        document,
                        "body",
                        PropertyValue.FromString(DocumentText(written)));
                    written++;
                }
                write.Commit();
            }

            string[] queries = Enumerable.Range(0, queryCount)
                .Select(i => $"topic{i % 97} common")
                .ToArray();
            GraphStats stats = database.CollectStats();
            using var read = database.BeginReadTransaction();
            var strict = read.Query;
            var wand = read.Query.WithStats(stats);
            bool strictEquivalent = true;
            for (int i = 0; i < Math.Min(50, queries.Length); i++)
            {
                strictEquivalent &= strict.Search(IndexName, queries[i], 20).ToList()
                    .SequenceEqual(wand.Search(IndexName, queries[i], 20).ToList());
            }

            var latencies = new double[queries.Length];
            for (int i = 0; i < queries.Length; i++)
            {
                long started = Stopwatch.GetTimestamp();
                _ = wand.Search(IndexName, queries[i], 20).ToList();
                latencies[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            Array.Sort(latencies);
            List<VertexId> beforeMerge =
                strict.Search(IndexName, "merge-marker", 20).ToList();

            using (var trigger = database.BeginWriteTransaction())
            {
                VertexId marker = trigger.CreateVertex("Doc");
                trigger.SetProperty(
                    marker,
                    "body",
                    PropertyValue.FromString("merge-marker common"));
                trigger.Commit();
            }
            backend.WaitForFullTextSegmentMergeForTest();
            if (backend.FullTextSegmentMergeErrorForTest is { } mergeError)
                throw new InvalidOperationException(
                    "Full-text segment merge failed.",
                    mergeError);
            using var afterRead = database.BeginReadTransaction();
            List<VertexId> afterMerge =
                afterRead.Query.Search(IndexName, "merge-marker", 20).ToList();
            bool mergeEquivalent = afterMerge.Count == beforeMerge.Count + 1;
            double publishP99 = publishDurations.Count == 0
                ? 0
                : Percentile(
                    publishDurations.Select(static duration => duration.TotalMilliseconds)
                        .Order()
                        .ToArray(),
                    0.99);
            double p50 = Percentile(latencies, 0.50);
            Console.WriteLine(
                $"fulltext_4_segment_search, p50_ms={p50:F3}, required_max={SearchGateMs:F2}, " +
                $"strict_equivalent={strictEquivalent}");
            Console.WriteLine(
                $"fulltext_merge_publish, merge_equivalent={mergeEquivalent}, " +
                $"publish_p99_ms={publishP99:F3}, required_max={PublishGateMs:F0}");
            read.Dispose();
            afterRead.Dispose();
            database.Dispose();
            using var reopened = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));
            using var reopenRead = reopened.BeginReadTransaction();
            bool reopenEquivalent = reopenRead.Query
                .Search(IndexName, "merge-marker", 20)
                .ToList()
                .SequenceEqual(afterMerge);
            long reopenPrimaryScans =
                ((BinaryGraphStorageBackend)reopened.BackendInternal)
                .FullTextPrimaryFallbackScanCountForTest;
            Console.WriteLine(
                $"fulltext_reopen, equivalent={reopenEquivalent}, " +
                $"primary_scans={reopenPrimaryScans}, required=0");
            return new(
                p50,
                publishP99,
                strictEquivalent,
                mergeEquivalent,
                reopenEquivalent && reopenPrimaryScans == 0,
                reopenPrimaryScans);
        }
        finally
        {
            BinaryGraphStorageBackend.FullTextSegmentPublishMeasuredForTest = null;
            BenchTempDir.Delete(dir);
        }
    }

    private static WriteAmplification MeasureWriteAmplification(int documentCount)
    {
        WriteBytes indexed = MeasureWriteBytes(documentCount, withIndex: true);
        WriteBytes primary = MeasureWriteBytes(documentCount, withIndex: false);
        double walAmplification = primary.Wal == 0
            ? double.NaN
            : indexed.Wal / (double)primary.Wal;
        double totalAmplification = primary.Wal == 0
            ? double.NaN
            : (indexed.Wal + indexed.Artifact) / (double)primary.Wal;
        Console.WriteLine(
            $"fulltext_ingest_write, documents={documentCount}, payload_wal_bytes={primary.Wal}, " +
            $"manifest_plus_payload_wal_bytes={indexed.Wal}, segment_body_bytes={indexed.Artifact}, " +
            $"wal_amplification={walAmplification:F2}, total_write_amplification={totalAmplification:F2}, " +
            $"required_max={WalAmplificationGate:F2}");
        return new(walAmplification, totalAmplification);
    }

    private static WriteBytes MeasureWriteBytes(int documentCount, bool withIndex)
    {
        string dir = BenchTempDir.Create(
            withIndex ? "product_fulltext_wal_indexed" : "product_fulltext_wal_primary");
        try
        {
            using var database = QuiverDatabase.Open(
                Path.Combine(dir, "graph.quiver"),
                new QuiverDatabaseOptions
                {
                    CheckpointThresholdBytes = long.MaxValue,
                });
            if (withIndex)
            {
                database.EditSchema(schema => schema.CreateIndex(
                    new FullTextIndexDefinition(
                        IndexName,
                        new PropertyTarget(
                            PropertyOwnerKind.Vertex,
                            "body",
                            "Doc"))));
            }
            for (int offset = 0; offset < documentCount; offset += 200)
            {
                using var write = database.BeginWriteTransaction();
                int end = Math.Min(documentCount, offset + 200);
                for (int i = offset; i < end; i++)
                {
                    VertexId document = write.CreateVertex("Doc");
                    write.SetProperty(
                        document,
                        "body",
                        PropertyValue.FromString(DocumentText(i)));
                }
                write.Commit();
            }
            string wal = Path.Combine(dir, "graph.quiver-wal");
            string artifact = Path.Combine(dir, "graph.quiver-ftseg");
            return new(
                File.Exists(wal) ? new FileInfo(wal).Length : 0,
                File.Exists(artifact) ? new FileInfo(artifact).Length : 0);
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static string DocumentText(int index)
        => $"topic{index % 97} common document {index} body text";

    private static int Parse(IReadOnlyList<string> args, int index, int fallback)
        => index < args.Count
            && int.TryParse(args[index], out int value)
            && value > 0
                ? value
                : fallback;

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private sealed record ProductResult(
        double SearchP50Ms,
        double PublishP99Ms,
        bool StrictEquivalent,
        bool MergeEquivalent,
        bool ReopenNoPrimaryScan,
        long ReopenPrimaryScans);

    private readonly record struct WriteBytes(long Wal, long Artifact);
    private readonly record struct WriteAmplification(double Wal, double Total);
}
