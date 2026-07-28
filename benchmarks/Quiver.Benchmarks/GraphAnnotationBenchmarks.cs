using System.Diagnostics;
using System.Runtime.InteropServices;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

internal static class GraphAnnotationBenchmarks
{
    private const double CaseLimitMilliseconds = 5_000;
    private const long CaseAllocationLimit = 128L * 1024 * 1024;
    private static readonly TimeSpan TotalLimit = TimeSpan.FromSeconds(45);

    internal static int Run()
    {
        var total = Stopwatch.StartNew();
        Console.WriteLine("=== Built-in graph annotations (product API) ===");
        Console.WriteLine(
            $"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} " +
            $"arch={RuntimeInformation.ProcessArchitecture} processorCount={Environment.ProcessorCount} " +
            "caseLimitMs=5000 allocationLimitBytes=134217728 totalLimitMs=45000 samples=3");
        try
        {
            RunExistingPathSentinel();
            StageReport last = RunStage(32, 128, 0x20080);
            foreach ((int vertices, int edges, int seed) in new[]
            {
                (128, 512, 0x80512),
                (1_000, 10_000, 0x10000),
                (5_000, 100_000, 0x50000),
            })
            {
                Projection projection = Project(last, vertices, edges, total);
                Console.WriteLine(
                    $"graph-annotation-projection fromVertices={last.VertexCount} fromEdges={last.EdgeCount} " +
                    $"toVertices={vertices} toEdges={edges} projectedMs={projection.Milliseconds:F3} " +
                    $"projectedAllocationBytes={projection.AllocationBytes} projectedTotalMs={projection.TotalMilliseconds:F3} " +
                    $"decision={(projection.Safe ? "RUN" : "SKIP")}");
                if (!projection.Safe) break;
                last = RunStage(vertices, edges, seed);
            }
            Console.WriteLine(
                $"graph-annotation-summary maxMeasuredVertices={last.VertexCount} maxMeasuredEdges={last.EdgeCount} " +
                $"totalMs={total.Elapsed.TotalMilliseconds:F3} existingPathStructure=UNCHANGED status=PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"graph-annotation-summary status=FAIL type={ex.GetType().Name} message={ex.Message}");
            return 1;
        }
    }

    private static StageReport RunStage(int vertexCount, int edgeCount, int seed)
    {
        using CaseData data = CreateDatabase(vertexCount, edgeCount, seed);
        GraphAnnotationOptions options = new()
        {
            EdgeType = "Arc",
            MaxVertices = vertexCount,
            MaxEdges = edgeCount,
            MaxResults = vertexCount,
            MaxRelaxations = edgeCount * 2L,
            MaxAnnotationUpdates = edgeCount * 2L,
            TimeLimit = TimeSpan.FromSeconds(10),
        };
        GraphAnnotationResult<double> Evaluate() => data.Read.EvaluateTropical(
            data.Vertices[0],
            GraphAnnotationPolicy.LabelSetting,
            static (transaction, edge) => transaction.GetProperty(edge, "weight").DoubleValue,
            options);

        _ = Evaluate();
        var elapsed = new double[3];
        GraphAnnotationResult<double> result = null!;
        for (int sample = 0; sample < elapsed.Length; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            result = Evaluate();
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Validate(result, vertexCount, edgeCount);
        }
        Array.Sort(elapsed);
        long before = GC.GetAllocatedBytesForCurrentThread();
        GraphAnnotationResult<double> allocationResult = Evaluate();
        long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Validate(allocationResult, vertexCount, edgeCount);
        double median = elapsed[1];
        double spread = median == 0 ? 0 : (elapsed[2] - elapsed[0]) / median * 100;
        if (median > CaseLimitMilliseconds || allocation > CaseAllocationLimit)
            throw new InvalidOperationException("case resource limit exceeded");
        Console.WriteLine(
            $"graph-annotation-stage vertices={vertexCount} edges={edgeCount} medianMs={median:F3} " +
            $"spreadPct={spread:F2} allocationBytes={allocation} relaxations={result.RelaxationCount} " +
            $"queuePeak={result.QueuePeak} annotations={result.TotalAnnotationCount} exact={result.IsExact.ToString().ToLowerInvariant()} " +
            $"reason={result.TerminationReason}");
        return new(vertexCount, edgeCount, median, allocation);
    }

    private static void RunExistingPathSentinel()
    {
        using CaseData data = CreateDatabase(128, 512, 0x51e71);
        VertexId source = data.Vertices[0];
        VertexId target = data.Vertices[^1];
        Measurement oneHop = Measure(() => ScanOneHop(data.Read, source), repetitions: 200);
        Measurement threeHop = Measure(() => ScanThreeHop(data.Read, source), repetitions: 20);
        Measurement weighted = Measure(
            () => BitConverter.DoubleToInt64Bits(data.Read.Query.WeightedShortestPath(source, target, "weight", type: "Arc").Distance),
            repetitions: 20);
        Console.WriteLine(
            "graph-annotation-existing-path note=baseline-only-no-annotation-hook comparison=separate-work-no-speed-ratio " +
            $"oneHopMs={oneHop.Milliseconds:F3} oneHopAllocationBytes={oneHop.AllocationBytes} " +
            $"threeHopMs={threeHop.Milliseconds:F3} threeHopAllocationBytes={threeHop.AllocationBytes} " +
            $"weightedPairMs={weighted.Milliseconds:F3} weightedPairAllocationBytes={weighted.AllocationBytes}");
    }

    private static Measurement Measure(Func<long> action, int repetitions)
    {
        _ = action();
        var samples = new double[3];
        long checksum = 0;
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int iteration = 0; iteration < repetitions; iteration++) checksum ^= action();
            samples[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / repetitions;
        }
        Array.Sort(samples);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < repetitions; iteration++) checksum ^= action();
        long allocation = (GC.GetAllocatedBytesForCurrentThread() - before) / repetitions;
        GC.KeepAlive(checksum);
        return new(samples[1], allocation);
    }

    private static long ScanOneHop(IReadTransaction transaction, VertexId source)
    {
        long checksum = 0;
        EdgeEnumerator edges = transaction.EnumerateEdges(source, Direction.Outgoing, "Arc");
        try { while (edges.MoveNext()) checksum = unchecked(checksum * 31 + edges.Current.Target.Value); }
        finally { edges.Dispose(); }
        return checksum;
    }

    private static long ScanThreeHop(IReadTransaction transaction, VertexId source)
    {
        long checksum = 0;
        EdgeEnumerator first = transaction.EnumerateEdges(source, Direction.Outgoing, "Arc");
        try
        {
            while (first.MoveNext())
            {
                EdgeEnumerator second = transaction.EnumerateEdges(first.Current.Target, Direction.Outgoing, "Arc");
                try
                {
                    while (second.MoveNext())
                    {
                        EdgeEnumerator third = transaction.EnumerateEdges(second.Current.Target, Direction.Outgoing, "Arc");
                        try { while (third.MoveNext()) checksum = unchecked(checksum * 31 + third.Current.Target.Value); }
                        finally { third.Dispose(); }
                    }
                }
                finally { second.Dispose(); }
            }
        }
        finally { first.Dispose(); }
        return checksum;
    }

    private static CaseData CreateDatabase(int vertexCount, int edgeCount, int seed)
    {
        QuiverDatabase database = QuiverDatabase.CreateInMemory();
        VertexId[] vertices;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            vertices = Enumerable.Range(0, vertexCount).Select(_ => write.CreateVertex("Node")).ToArray();
            var random = new Random(seed);
            for (int edge = 0; edge < edgeCount; edge++)
            {
                int source = edge < vertexCount - 1 ? edge : random.Next(vertexCount);
                int target = edge < vertexCount - 1 ? edge + 1 : random.Next(vertexCount);
                if (source == target) target = (target + 1) % vertexCount;
                EdgeId id = write.CreateEdge(vertices[source], vertices[target], "Arc");
                write.SetProperty(id, "weight", PropertyValue.FromDouble(0.1 + random.NextDouble() * 9.9));
            }
            write.Commit();
        }
        return new(database, database.BeginReadTransaction(), vertices);
    }

    private static void Validate(GraphAnnotationResult<double> result, int vertexCount, int edgeCount)
    {
        if (!result.IsExact || result.TerminationReason != GraphAnnotationTerminationReason.Completed
            || result.VertexCount != vertexCount || result.EdgeCount != edgeCount
            || result.TotalAnnotationCount != vertexCount || result.RelaxationCount > edgeCount * 2L)
            throw new InvalidOperationException("product annotation result violated its contract");
    }

    private static Projection Project(StageReport previous, int nextVertices, int nextEdges, Stopwatch total)
    {
        double scale = Math.Max((double)nextVertices / previous.VertexCount, (double)nextEdges / previous.EdgeCount);
        double milliseconds = previous.Milliseconds * scale * 2;
        long allocation = (long)Math.Ceiling(previous.AllocationBytes * scale * 2);
        double projectedTotal = total.Elapsed.TotalMilliseconds + milliseconds * 4;
        return new(milliseconds, allocation, projectedTotal,
            milliseconds <= CaseLimitMilliseconds && allocation <= CaseAllocationLimit && projectedTotal <= TotalLimit.TotalMilliseconds);
    }

    private sealed record CaseData(QuiverDatabase Database, IReadTransaction Read, VertexId[] Vertices) : IDisposable
    {
        public void Dispose() { Read.Dispose(); Database.Dispose(); }
    }

    private readonly record struct Measurement(double Milliseconds, long AllocationBytes);
    private readonly record struct StageReport(int VertexCount, int EdgeCount, double Milliseconds, long AllocationBytes);
    private readonly record struct Projection(double Milliseconds, long AllocationBytes, double TotalMilliseconds, bool Safe);
}
