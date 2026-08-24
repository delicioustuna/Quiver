using System.Diagnostics;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Benchmarks;

public static class DirectedNexusAlgorithmBenchmarks
{
    private const int CaseMillisecondsLimit = 5_000;
    private const long CaseAllocationLimit = 128L * 1024 * 1024;
    private static readonly TimeSpan TotalLimit = TimeSpan.FromSeconds(45);

    public static int Run()
    {
        Console.WriteLine("=== Directed Nexus algorithms (product API) ===");
        Console.WriteLine("samples=3 warmup=1 caseLimitMs=5000 allocationLimit=134217728 totalLimitMs=45000");
        var total = Stopwatch.StartNew();
        StageReport? previous = null;
        foreach (int nexusCount in new[] { 1_000, 10_000, 25_000 })
        {
            if (previous is not null && !CanRun(previous, nexusCount, total, out string reason))
            {
                Console.WriteLine($"directed-nexus-stage nexus={nexusCount} status=SKIP reason={reason}");
                continue;
            }
            previous = MeasureStage(nexusCount);
        }
        Console.WriteLine($"directed-nexus-total elapsedMs={total.Elapsed.TotalMilliseconds:F3} result=PASS");
        return 0;
    }

    private static StageReport MeasureStage(int nexusCount)
    {
        using var db = YatagarasuDatabase.CreateInMemory();
        VertexId common, initial, current;
        using (var write = db.BeginWriteTransaction())
        {
            common = write.CreateVertex("Item");
            initial = write.CreateVertex("Item");
            current = initial;
            for (int i = 0; i < nexusCount; i++)
            {
                VertexId next = write.CreateVertex("Item");
                NexusId nexus = write.CreateNexus("Rule", [
                    new("tail", common), new("tail", current), new("head", next)]);
                write.SetProperty(nexus, "cost", PropertyValue.FromDouble(1));
                current = next;
            }
            write.Commit();
        }

        using var read = db.BeginReadTransaction();
        VertexId target = current;
        VertexId[] seeds = [common, initial];
        Metric reach = Measure(() =>
        {
            var result = read.FindReachableVertices(seeds, "Rule", "tail", "head",
                new() { MaxNexuses = nexusCount + 1, MaxResults = nexusCount + 2 });
            if (!result.IsComplete || result.Vertices.Count != nexusCount + 2) throw new InvalidOperationException("reachability result differs");
            return result.Vertices.Count;
        });
        Metric additive = Measure(() => MeasureDerivation(read, seeds, target, nexusCount, DerivationCostMode.Additive));
        Metric bottleneck = Measure(() => MeasureDerivation(read, seeds, target, nexusCount, DerivationCostMode.Bottleneck));
        Console.WriteLine(
            $"directed-nexus-stage nexus={nexusCount} reachMs={reach.Milliseconds:F3} reachAlloc={reach.AllocatedBytes} " +
            $"additiveMs={additive.Milliseconds:F3} additiveAlloc={additive.AllocatedBytes} " +
            $"bottleneckMs={bottleneck.Milliseconds:F3} bottleneckAlloc={bottleneck.AllocatedBytes} " +
            $"treeNodes={additive.Checksum} status=PASS");
        return new(nexusCount, Math.Max(reach.Milliseconds, Math.Max(additive.Milliseconds, bottleneck.Milliseconds)),
            Math.Max(reach.AllocatedBytes, Math.Max(additive.AllocatedBytes, bottleneck.AllocatedBytes)));
    }

    private static long MeasureDerivation(
        IReadTransaction read,
        VertexId[] seeds,
        VertexId target,
        int nexusCount,
        DerivationCostMode mode)
    {
        var result = read.FindShortestDerivation(
            seeds, target, "Rule", "tail", "head", static (tx, nexus) => tx.GetProperty(nexus, "cost").DoubleValue,
            mode, new() { MaxNexuses = nexusCount + 1, MaxTreeNodes = nexusCount * 2 + 2 });
        double expected = mode == DerivationCostMode.Additive ? nexusCount : 1;
        if (!result.IsComplete || result.Cost != expected || result.Tree.Count != nexusCount * 2 + 1)
            throw new InvalidOperationException("derivation result differs");
        return result.Tree.Count;
    }

    private static Metric Measure(Func<long> action)
    {
        _ = action();
        var samples = new Metric[3];
        for (int i = 0; i < samples.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            long checksum = action();
            samples[i] = new(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - before, checksum);
        }
        return samples.OrderBy(sample => sample.Milliseconds).ElementAt(1);
    }

    private static bool CanRun(StageReport current, int nextCount, Stopwatch total, out string reason)
    {
        double ratio = nextCount / (double)current.NexusCount;
        double projectedMs = current.MaximumMilliseconds * ratio * 1.5;
        long projectedAllocation = (long)Math.Ceiling(current.MaximumAllocation * ratio * 1.5);
        bool safe = projectedMs <= CaseMillisecondsLimit
            && projectedAllocation <= CaseAllocationLimit
            && total.Elapsed.TotalMilliseconds + projectedMs * 3 < TotalLimit.TotalMilliseconds;
        reason = safe ? "within-conservative-budget" : $"projection-ms-{projectedMs:F0}-allocation-{projectedAllocation}";
        return safe;
    }

    private readonly record struct Metric(double Milliseconds, long AllocatedBytes, long Checksum);
    private sealed record StageReport(int NexusCount, double MaximumMilliseconds, long MaximumAllocation);
}
