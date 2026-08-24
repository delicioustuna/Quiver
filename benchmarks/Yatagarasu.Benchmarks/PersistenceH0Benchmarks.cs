using System.Diagnostics;
using System.Runtime.InteropServices;
using Yatagarasu.Core;

namespace Yatagarasu.Benchmarks;

internal static class PersistenceH0Benchmarks
{
    private const double CaseTimeLimitMilliseconds = 10_000;
    private const long CaseAllocationLimit = 500L * 1024 * 1024;
    private static readonly TimeSpan TotalLimit = TimeSpan.FromSeconds(45);

    internal static int Run()
    {
        var total = Stopwatch.StartNew();
        Console.WriteLine("=== Persistence H0 (product API) ===");
        Console.WriteLine(
            $"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} " +
            $"arch={RuntimeInformation.ProcessArchitecture} processorCount={Environment.ProcessorCount} " +
            "filtration=SparseKnn neighbors=16 maxPoints=1000 maxEdges=1000000 " +
            "maxDistanceEvaluations=1000000 maxResults=1000 timeLimitMs=10000 " +
            "caseLimitMs=10000 allocationLimitBytes=524288000 totalLimitMs=45000 samples=3 warmup=1");
        try
        {
            StageReport previous = MeasureStage(100, 32);
            int maxMeasured = previous.PointCount;
            foreach ((int points, int dimensions) in new[] { (250, 384), (1_000, 384) })
            {
                Projection projection = Project(previous, points, dimensions, total);
                if (!projection.Safe)
                {
                    PrintProjection(projection, "SKIP");
                    break;
                }
                PrintProjection(projection, "RUN");
                previous = MeasureStage(points, dimensions);
                maxMeasured = points;
            }
            Console.WriteLine(
                $"persistence-h0-summary maxMeasuredPoints={maxMeasured} " +
                $"totalMs={total.Elapsed.TotalMilliseconds:F3} status=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"persistence-h0-summary status=FAIL type={exception.GetType().Name} message={exception.Message}");
            return 1;
        }
    }

    private static StageReport MeasureStage(int pointCount, int dimensions)
    {
        using CaseData data = CreateDatabase(pointCount, dimensions);
        PersistenceH0Options options = Options(pointCount);
        _ = Compute(data.Read, options);
        var samples = new Measurement[3];
        for (int i = 0; i < samples.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            PersistenceH0Result result = Compute(data.Read, options);
            samples[i] = new(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - before,
                result);
        }
        Measurement median = samples.OrderBy(static sample => sample.Milliseconds).ElementAt(1);
        double spread = (samples.Max(static sample => sample.Milliseconds)
            - samples.Min(static sample => sample.Milliseconds)) / median.Milliseconds * 100;
        if (median.Milliseconds > CaseTimeLimitMilliseconds
            || median.AllocatedBytes > CaseAllocationLimit)
        {
            throw new InvalidOperationException("case resource safety cap exceeded");
        }
        Console.WriteLine(
            $"persistence-h0-stage points={pointCount} dimensions={dimensions} neighbors=16 " +
            $"medianMs={median.Milliseconds:F3} spreadPct={spread:F2} allocBytes={median.AllocatedBytes} " +
            $"edges={median.Result.EdgeCount} intervals={median.Result.Intervals.Count} " +
            $"components={median.Result.ComponentCount} kind={median.Result.Kind} " +
            $"reason={median.Result.TerminationReason} status=PASS");
        return new(pointCount, dimensions, median.Milliseconds, median.AllocatedBytes);
    }

    private static PersistenceH0Result Compute(IReadTransaction read, PersistenceH0Options options)
    {
        PersistenceH0Result result = PersistenceH0Algorithms.Compute(read, "points", options);
        if (!result.IsComplete
            || result.Kind != PersistenceH0ResultKind.SparseApproximation
            || result.PointCount != result.Intervals.Count)
        {
            throw new InvalidOperationException("product API returned an incomplete or mislabeled result");
        }
        return result;
    }

    private static PersistenceH0Options Options(int pointCount) => new()
    {
        Filtration = PersistenceH0Filtration.SparseKnn,
        NeighborCount = 16,
        MaxPoints = 1_000,
        MaxEdges = 1_000_000,
        MaxDistanceEvaluations = 1_000_000,
        MaxResults = 1_000,
        TimeLimit = TimeSpan.FromSeconds(10),
    };

    private static Projection Project(
        StageReport previous,
        int nextPoints,
        int nextDimensions,
        Stopwatch total)
    {
        double scale = (double)nextPoints * nextPoints * nextDimensions
            / ((double)previous.PointCount * previous.PointCount * previous.Dimensions);
        double milliseconds = previous.Milliseconds * scale * 1.5;
        long allocation = (long)Math.Ceiling(previous.AllocatedBytes * scale * 1.5);
        double totalMilliseconds = total.Elapsed.TotalMilliseconds + milliseconds * 4;
        bool safe = milliseconds <= CaseTimeLimitMilliseconds
            && allocation <= CaseAllocationLimit
            && totalMilliseconds <= TotalLimit.TotalMilliseconds;
        return new(previous.PointCount, previous.Dimensions, nextPoints, nextDimensions,
            milliseconds, allocation, totalMilliseconds, safe);
    }

    private static void PrintProjection(Projection projection, string decision) =>
        Console.WriteLine(
            $"persistence-h0-projection fromPoints={projection.FromPoints} " +
            $"fromDimensions={projection.FromDimensions} toPoints={projection.ToPoints} " +
            $"toDimensions={projection.ToDimensions} projectedCaseMs={projection.Milliseconds:F3} " +
            $"projectedAllocationBytes={projection.AllocatedBytes} " +
            $"projectedTotalMs={projection.TotalMilliseconds:F3} decision={decision}");

    private static CaseData CreateDatabase(int pointCount, int dimensions)
    {
        YatagarasuDatabase database = YatagarasuDatabase.CreateInMemory();
        database.EditSchema(schema => schema.CreateIndex(new VectorIndexDefinition(
            "points",
            new PropertyTarget(PropertyOwnerKind.Vertex, "position", "Point"),
            dimensions,
            DistanceMetric.Euclidean)));
        var random = new Random(0x50420000 + pointCount);
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            for (int point = 0; point < pointCount; point++)
            {
                VertexId owner = write.CreateVertex("Point");
                var vector = new float[dimensions];
                for (int dimension = 0; dimension < dimensions; dimension++)
                    vector[dimension] = (float)(2 * random.NextDouble() - 1);
                write.SetVectorProperty(EntityRef.From(owner), "position", vector);
            }
            write.Commit();
        }
        return new(database, database.BeginReadTransaction());
    }

    private readonly record struct Measurement(
        double Milliseconds,
        long AllocatedBytes,
        PersistenceH0Result Result);
    private sealed record StageReport(
        int PointCount,
        int Dimensions,
        double Milliseconds,
        long AllocatedBytes);
    private readonly record struct Projection(
        int FromPoints,
        int FromDimensions,
        int ToPoints,
        int ToDimensions,
        double Milliseconds,
        long AllocatedBytes,
        double TotalMilliseconds,
        bool Safe);

    private sealed record CaseData(YatagarasuDatabase Database, IReadTransaction Read) : IDisposable
    {
        public void Dispose()
        {
            Read.Dispose();
            Database.Dispose();
        }
    }
}
