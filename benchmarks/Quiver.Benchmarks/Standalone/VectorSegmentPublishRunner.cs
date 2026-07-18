using System.Collections.Concurrent;
using System.Diagnostics;
using Quiver.Core;

namespace Quiver.Benchmarks.Standalone;

internal static class VectorSegmentPublishRunner
{
    internal static int Run(string[] args)
    {
        int vectorCount = Parse(args, 0, 10_000);
        int dimensions = Parse(args, 1, 384);
        string directory = Path.Combine(
            Path.GetTempPath(),
            "quiver_vector_publish_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.quiver");
        var buildStarted = new ManualResetEventSlim();
        var buildCompleted = new ManualResetEventSlim();
        var publishDurations = new ConcurrentQueue<double>();
        BinaryGraphStorageBackend.VectorSegmentBuildStartedForTest =
            buildStarted.Set;
        BinaryGraphStorageBackend.VectorSegmentBuildCompletedForTest =
            buildCompleted.Set;
        BinaryGraphStorageBackend.VectorSegmentPublishMeasuredForTest =
            elapsed => publishDurations.Enqueue(elapsed.TotalMilliseconds);

        try
        {
            using var database = QuiverDatabase.Open(path);
            using (var schema = database.BeginWriteTransaction())
            {
                schema.EditSchema.CreateIndex(new VectorIndexDefinition(
                    "embedding_idx",
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        "embedding",
                        "Document"),
                    dimensions,
                    SegmentPolicy: new VectorSegmentPolicy(
                        MaximumDeltaEntries: 1,
                        MaximumSegments: 2)));
                schema.Commit();
            }

            var random = new Random(0x715E6D);
            var vector = new float[dimensions];
            using (var write = database.BeginWriteTransaction())
            {
                for (int i = 0; i < vectorCount; i++)
                {
                    VertexId owner = write.CreateVertex("Document");
                    for (int d = 0; d < vector.Length; d++)
                        vector[d] = (float)(random.NextDouble() * 2 - 1);
                    write.SetVectorProperty(
                        EntityRef.From(owner),
                        "embedding",
                        vector);
                }
                write.Commit();
            }

            if (!buildStarted.Wait(TimeSpan.FromSeconds(30)))
            {
                Console.WriteLine(
                    "vector_segment_publish, result=FAIL, reason=build_not_started");
                return 1;
            }

            var writerLatencies = new List<double>();
            while (!buildCompleted.IsSet)
            {
                var timer = Stopwatch.StartNew();
                using var write = database.BeginWriteTransaction();
                write.CreateVertex("WriterProbe");
                write.Commit();
                timer.Stop();
                writerLatencies.Add(timer.Elapsed.TotalMilliseconds);
            }

            var backend = (BinaryGraphStorageBackend)database.BackendInternal;
            backend.WaitForVectorSegmentMergeForTest();
            if (backend.VectorSegmentMergeErrorForTest is { } mergeError)
            {
                Console.WriteLine(
                    $"vector_segment_publish, result=FAIL, reason={mergeError.GetType().Name}");
                return 1;
            }

            double writerP99 = Percentile(writerLatencies, 0.99);
            double publishP99 = Percentile(publishDurations.ToArray(), 0.99);
            double maximumPublishMs =
                new QuiverDatabaseOptions().LockTimeout.TotalMilliseconds * 0.10;
            bool passed = writerLatencies.Count > 0
                && publishDurations.Count > 0
                && publishP99 <= maximumPublishMs;
            Console.WriteLine(
                "vector_segment_publish, "
                + $"vectors={vectorCount}, dimensions={dimensions}, "
                + $"writers_during_build={writerLatencies.Count}, "
                + $"writer_p99_ms={writerP99:F3}, "
                + $"publish_p99_ms={publishP99:F3}, "
                + $"required_publish_max_ms={maximumPublishMs:F3}, "
                + $"result={(passed ? "PASS" : "FAIL")}");
            return passed ? 0 : 1;
        }
        finally
        {
            BinaryGraphStorageBackend.VectorSegmentBuildStartedForTest = null;
            BinaryGraphStorageBackend.VectorSegmentBuildCompletedForTest = null;
            BinaryGraphStorageBackend.VectorSegmentPublishMeasuredForTest = null;
            buildStarted.Dispose();
            buildCompleted.Dispose();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static int Parse(string[] args, int index, int fallback)
        => args.Length > index
            && int.TryParse(args[index], out int value)
            && value > 0
                ? value
                : fallback;

    private static double Percentile(
        IReadOnlyCollection<double> values,
        double percentile)
    {
        if (values.Count == 0)
            return double.PositiveInfinity;
        double[] ordered = values.Order().ToArray();
        int index = Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Length) - 1,
            0,
            ordered.Length - 1);
        return ordered[index];
    }
}
