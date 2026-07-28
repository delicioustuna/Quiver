using System.Diagnostics;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Query.Optimizer;

namespace Quiver.Benchmarks;

internal static class CyclicTriangleJoinBenchmarks
{
    private const long AllocationEnvelope = 128L * 1024 * 1024;
    private static readonly TimeSpan CaseEnvelope = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RunnerEnvelope = TimeSpan.FromSeconds(45);

    public static int Run()
    {
        long runnerStart = Stopwatch.GetTimestamp();
        Console.WriteLine("=== Cyclic triangle join product benchmark ===");
        Console.WriteLine("boundary=three public Match extractions plus internal fixed-triangle optimizer");
        Console.WriteLine("envelope=5 s/case, 128 MiB thread allocation, 45 s total, warmup=1, samples=3");

        try
        {
            var rows = new List<Stage>();
            rows.Add(MeasureStage(32));
            rows.Add(MeasureStage(64));

            Projection projection = Project(rows[0], rows[1]);
            Console.WriteLine(
                $"cyclic-triangle-projection fromDomain=64 toDomain=128 projectedMs={projection.Milliseconds:F3} " +
                $"projectedAllocation={projection.Allocation} decision={(projection.Safe ? "MEASURE" : "SKIP")}");
            if (projection.Safe && Stopwatch.GetElapsedTime(runnerStart) < TimeSpan.FromSeconds(40))
                rows.Add(MeasureStage(128));

            bool passed = rows.All(static row => row.DigestMatch
                && row.Strategy == CyclicTriangleJoinStrategy.TransientColumns
                && row.CandidateResult.TerminationReason == CyclicTriangleJoinTerminationReason.Completed
                && row.BaselineResult.TerminationReason == CyclicTriangleJoinTerminationReason.Completed);
            Console.WriteLine(
                $"cyclic-triangle-summary maximumMeasuredDomain={rows[^1].Domain} " +
                $"runnerElapsedMs={Stopwatch.GetElapsedTime(runnerStart).TotalMilliseconds:F3} " +
                $"status={(passed ? "PASS" : "FAIL")}");
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"cyclic-triangle-summary status=FAIL type={exception.GetType().Name} message={exception.Message}");
            return 1;
        }
    }

    private static Stage MeasureStage(int domain)
    {
        using var dataset = Dataset.Create(domain);
        _ = dataset.Execute(materializing: true);
        _ = dataset.Execute(materializing: false);

        Measurement baseline = Measure(() => dataset.Execute(materializing: true));
        Measurement candidate = Measure(() => dataset.Execute(materializing: false));
        CyclicTriangleJoinResult baselineCheck = dataset.Execute(materializing: true);
        CyclicTriangleJoinResult candidateCheck = dataset.Execute(materializing: false);
        bool digestMatch = Digest(baselineCheck.Rows) == Digest(candidateCheck.Rows);
        var stage = new Stage(domain, baseline, candidate, candidateCheck.Strategy,
            baselineCheck, candidateCheck, digestMatch);

        Console.WriteLine(
            $"cyclic-triangle-stage domain={domain} output={candidateCheck.Rows.Length} " +
            $"estimatedIntermediate={candidateCheck.EstimatedIntermediateRows} " +
            $"baseline={baseline.Milliseconds:F3}ms/{baseline.AllocatedBytes}B " +
            $"transient={candidate.Milliseconds:F3}ms/{candidate.AllocatedBytes}B " +
            $"speedup={stage.Speedup:F3}x allocationRatio={stage.AllocationRatio:F3} " +
            $"peak={baselineCheck.PeakWorkingRows}/{candidateCheck.PeakWorkingRows} " +
            $"route={candidateCheck.Strategy}/{candidateCheck.RouteReason} " +
            $"digest={(digestMatch ? "PASS" : "FAIL")}");
        return stage;
    }

    private static Measurement Measure(Func<CyclicTriangleJoinResult> operation)
    {
        var samples = new Measurement[3];
        for (int i = 0; i < samples.Length; i++)
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            CyclicTriangleJoinResult result = operation();
            double nanoseconds = (Stopwatch.GetTimestamp() - start) * 1_000_000_000.0 / Stopwatch.Frequency;
            long allocation = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            samples[i] = new(nanoseconds, allocation, Digest(result.Rows));
        }
        Array.Sort(samples, static (left, right) => left.Nanoseconds.CompareTo(right.Nanoseconds));
        return samples[1];
    }

    private static Projection Project(Stage previous, Stage current)
    {
        double growth = Math.Max(2, current.Baseline.Nanoseconds / previous.Baseline.Nanoseconds);
        double allocationGrowth = Math.Max(2,
            (double)current.Baseline.AllocatedBytes / previous.Baseline.AllocatedBytes);
        double projectedMilliseconds = current.Baseline.Milliseconds * growth * 1.25;
        long projectedAllocation = (long)Math.Ceiling(current.Baseline.AllocatedBytes * allocationGrowth * 1.25);
        bool safe = projectedMilliseconds <= CaseEnvelope.TotalMilliseconds
            && projectedAllocation <= AllocationEnvelope;
        return new(safe, projectedMilliseconds, projectedAllocation);
    }

    private static ulong Digest(IReadOnlyList<CyclicTriangleJoinRow> rows)
    {
        ulong digest = (ulong)rows.Count;
        foreach (CyclicTriangleJoinRow row in rows)
        {
            digest = Mix(digest ^ (ulong)row.First.Value);
            digest = Mix(digest ^ (ulong)row.Second.Value);
            digest = Mix(digest ^ (ulong)row.Third.Value);
        }
        return digest;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private sealed class Dataset : IDisposable
    {
        private readonly string _directory;
        private readonly QuiverDatabase _database;

        private Dataset(string directory, QuiverDatabase database)
        {
            _directory = directory;
            _database = database;
        }

        public static Dataset Create(int domain)
        {
            string directory = BenchTempDir.Create("cyclic_triangle_join");
            Directory.CreateDirectory(directory);
            QuiverDatabase database = QuiverDatabase.Open(Path.Combine(directory, "graph.quiver"));
            try
            {
                using var write = database.BeginWriteTransaction();
                VertexId[] first = CreateVertices(write, "A", domain);
                VertexId[] second = CreateVertices(write, "B", domain);
                VertexId[] third = CreateVertices(write, "C", domain);
                for (int i = 0; i < domain; i++)
                {
                    for (int j = 0; j < domain; j++)
                    {
                        write.CreateEdge(first[i], second[j], "R");
                        write.CreateEdge(second[i], third[j], "S");
                    }
                    write.CreateEdge(third[i], first[i], "T");
                }
                write.Commit();
                return new(directory, database);
            }
            catch
            {
                database.Dispose();
                BenchTempDir.Delete(directory);
                throw;
            }
        }

        public CyclicTriangleJoinResult Execute(bool materializing)
        {
            using var read = _database.BeginReadTransaction();
            List<CyclicTriangleJoinPair> first = read.Query.Match(
                    GraphPattern.Vertex("a", "A").Out("R", GraphPattern.Vertex("b", "B")))
                .Return(ctx => new CyclicTriangleJoinPair(ctx.Vertex("a"), ctx.Vertex("b"))).ToList();
            List<CyclicTriangleJoinPair> second = read.Query.Match(
                    GraphPattern.Vertex("b", "B").Out("S", GraphPattern.Vertex("c", "C")))
                .Return(ctx => new CyclicTriangleJoinPair(ctx.Vertex("b"), ctx.Vertex("c"))).ToList();
            List<CyclicTriangleJoinPair> closing = read.Query.Match(
                    GraphPattern.Vertex("c", "C").Out("T", GraphPattern.Vertex("a", "A")))
                .Return(ctx => new CyclicTriangleJoinPair(ctx.Vertex("c"), ctx.Vertex("a"))).ToList();
            var options = new CyclicTriangleJoinOptions
            {
                MaxResults = int.MaxValue,
                MaxWork = long.MaxValue,
                MaxMaterializedIntermediateRows = 3_000_000,
                TimeLimit = CaseEnvelope,
            };
            return materializing
                ? CyclicTriangleJoinOptimizer.ExecuteMaterializingForComparison(first, second, closing, options)
                : CyclicTriangleJoinOptimizer.Execute(first, second, closing, options);
        }

        public void Dispose()
        {
            _database.Dispose();
            BenchTempDir.Delete(_directory);
        }

        private static VertexId[] CreateVertices(IWriteTransaction write, string label, int count)
        {
            var vertices = new VertexId[count];
            for (int i = 0; i < count; i++) vertices[i] = write.CreateVertex(label);
            return vertices;
        }
    }

    private readonly record struct Measurement(double Nanoseconds, long AllocatedBytes, ulong Digest)
    {
        public double Milliseconds => Nanoseconds / 1_000_000;
    }

    private readonly record struct Stage(
        int Domain,
        Measurement Baseline,
        Measurement Candidate,
        CyclicTriangleJoinStrategy Strategy,
        CyclicTriangleJoinResult BaselineResult,
        CyclicTriangleJoinResult CandidateResult,
        bool DigestMatch)
    {
        public double Speedup => Baseline.Nanoseconds / Candidate.Nanoseconds;
        public double AllocationRatio => (double)Candidate.AllocatedBytes / Baseline.AllocatedBytes;
    }

    private readonly record struct Projection(bool Safe, double Milliseconds, long Allocation);
}
