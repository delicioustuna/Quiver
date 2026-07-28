using System.Diagnostics;
using System.Runtime.InteropServices;
using Quiver.Core;

namespace Quiver.Benchmarks;

internal static class FormalConceptBenchmarks
{
    private const double CaseLimitMilliseconds = 10_000;
    private const long AllocationLimit = 500L * 1024 * 1024;
    private static readonly TimeSpan TotalLimit = TimeSpan.FromSeconds(45);

    internal static int Run()
    {
        var total = Stopwatch.StartNew();
        Console.WriteLine("=== Formal concepts (product API) ===");
        Console.WriteLine(
            $"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} " +
            $"arch={RuntimeInformation.ProcessArchitecture} processorCount={Environment.ProcessorCount} " +
            "objects=100 attributes=32,64 density=block-correlated strategies=NextClosure,CloseByOne pageSizes=100000,7 " +
            "maxResults=100000 maxClosureEvaluations=2000000 maxObjects=1000 maxAttributes=4096 " +
            "maxIncidences=1000000 timeLimitMs=10000 caseLimitMs=10000 allocationLimitBytes=524288000 " +
            "totalLimitMs=45000 samples=3 warmup=1");
        try
        {
            Stage first = Measure(32);
            Projection projection = Project(first, 64, total);
            Console.WriteLine($"formal-concepts-projection fromAttributes=32 toAttributes=64 " +
                $"projectedCaseMs={projection.Milliseconds:F3} projectedAllocationBytes={projection.Allocation} " +
                $"projectedTotalMs={projection.TotalMilliseconds:F3} decision={(projection.Safe ? "RUN" : "SKIP")}");
            int maximum = 32;
            if (projection.Safe) { _ = Measure(64); maximum = 64; }
            Console.WriteLine($"formal-concepts-summary maxMeasuredAttributes={maximum} totalMs={total.Elapsed.TotalMilliseconds:F3} status=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"formal-concepts-summary status=FAIL type={exception.GetType().Name} message={exception.Message}");
            return 1;
        }
    }

    private static Stage Measure(int attributes)
    {
        using CaseData data = Create(100, attributes);
        FormalConceptOptions nextOptions = Options(FormalConceptEnumerationStrategy.NextClosure);
        FormalConceptOptions closeOptions = Options(FormalConceptEnumerationStrategy.CloseByOne);
        _ = Compute(data.Read, nextOptions);
        _ = Compute(data.Read, closeOptions);
        Measurement next = MeasureStrategy(data.Read, nextOptions);
        Measurement close = MeasureStrategy(data.Read, closeOptions);
        Measurement paged = MeasurePaged(data.Read, nextOptions with { PageSize = 7 });
        long maximumAllocation = Math.Max(next.Allocation, Math.Max(close.Allocation, paged.Allocation));
        double maximumMilliseconds = Math.Max(next.Milliseconds, Math.Max(close.Milliseconds, paged.Milliseconds));
        if (maximumMilliseconds > CaseLimitMilliseconds || maximumAllocation > AllocationLimit)
            throw new InvalidOperationException("case resource safety cap exceeded");
        Console.WriteLine($"formal-concepts-stage objects=100 attributes={attributes} concepts={next.Result.Concepts.Count} " +
            $"nextMs={next.Milliseconds:F3} nextAllocBytes={next.Allocation} closeByOneMs={close.Milliseconds:F3} " +
            $"closeByOneAllocBytes={close.Allocation} timeRatio={next.Milliseconds / Math.Max(close.Milliseconds, 1e-9):F3} " +
            $"allocationRatio={next.Allocation / (double)Math.Max(close.Allocation, 1):F3} paged7Ms={paged.Milliseconds:F3} " +
            $"paged7AllocBytes={paged.Allocation} closureEvaluations={next.Result.ClosureEvaluations} status=PASS");
        return new(attributes, maximumMilliseconds, maximumAllocation);
    }

    private static Measurement MeasureStrategy(IReadTransaction read, FormalConceptOptions options)
    {
        var measurements = new Measurement[3];
        for (int i = 0; i < measurements.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            FormalConceptResult result = Compute(read, options);
            measurements[i] = new(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - before, result);
        }
        Measurement median = measurements.OrderBy(static value => value.Milliseconds).ElementAt(1);
        return median;
    }

    private static Measurement MeasurePaged(IReadTransaction read, FormalConceptOptions options)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        FormalConceptContinuation? token = null;
        FormalConceptResult last;
        int concepts = 0;
        do
        {
            last = read.EnumerateFormalConcepts("Attribute", "object", options, token);
            concepts += last.Concepts.Count;
            token = last.Continuation;
        }
        while (token is not null);
        if (!last.IsComplete) throw new InvalidOperationException("paged enumeration did not complete");
        var synthetic = new FormalConceptResult([], null, last.TerminationReason, last.ObjectCount,
            last.AttributeCount, last.IncidenceCount, last.ClosureEvaluations, last.ContextFingerprint, last.TransactionId);
        return new(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - before, synthetic);
    }

    private static FormalConceptResult Compute(IReadTransaction read, FormalConceptOptions options)
    {
        FormalConceptResult result = read.EnumerateFormalConcepts("Attribute", "object", options);
        if (!result.IsComplete || result.Continuation is not null)
            throw new InvalidOperationException("product API returned an incomplete result");
        return result;
    }

    private static CaseData Create(int objects, int attributes)
    {
        var database = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            VertexId[] ids = Enumerable.Range(0, objects).Select(_ => write.CreateVertex("Object")).ToArray();
            var random = new Random(0x46434100 + attributes);
            for (int attribute = 0; attribute < attributes; attribute++)
            {
                int block = attribute % 8;
                NexusMember[] members = Enumerable.Range(0, objects)
                    .Where(obj => obj % 8 == block || random.NextDouble() < 0.08)
                    .Select(obj => new NexusMember("object", ids[obj]))
                    .ToArray();
                write.CreateNexus("Attribute", members);
            }
            write.Commit();
        }
        return new(database, database.BeginReadTransaction());
    }

    private static FormalConceptOptions Options(FormalConceptEnumerationStrategy strategy) => new()
    {
        Strategy = strategy,
        PageSize = 100_000,
        MaxResults = 100_000,
        MaxClosureEvaluations = 2_000_000,
        MaxObjects = 1_000,
        MaxAttributes = 4_096,
        MaxIncidences = 1_000_000,
        TimeLimit = TimeSpan.FromSeconds(10),
    };

    private static Projection Project(Stage previous, int attributes, Stopwatch total)
    {
        double scale = (double)attributes * attributes / (previous.Attributes * previous.Attributes);
        double milliseconds = previous.Milliseconds * scale * 1.5;
        long allocation = (long)Math.Ceiling(previous.Allocation * scale * 1.5);
        double totalMilliseconds = total.Elapsed.TotalMilliseconds + milliseconds * 4;
        return new(milliseconds, allocation, totalMilliseconds,
            milliseconds <= CaseLimitMilliseconds && allocation <= AllocationLimit && totalMilliseconds <= TotalLimit.TotalMilliseconds);
    }

    private readonly record struct Measurement(double Milliseconds, long Allocation, FormalConceptResult Result);
    private readonly record struct Stage(int Attributes, double Milliseconds, long Allocation);
    private readonly record struct Projection(double Milliseconds, long Allocation, double TotalMilliseconds, bool Safe);
    private sealed record CaseData(QuiverDatabase Database, IReadTransaction Read) : IDisposable
    {
        public void Dispose() { Read.Dispose(); Database.Dispose(); }
    }
}
