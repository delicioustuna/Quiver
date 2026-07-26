using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Quiver.Index;

namespace Quiver.Benchmarks;

/// <summary>
/// B+Tree write-path allocation reduction.
/// Measures Insert/Delete allocation on Int64 keys (the dominant hot path).
/// </summary>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3)]
[MemoryDiagnoser]
public class BTreeInsertBenchmarks
{
    [Params(10_000, 100_000, 1_000_000)]
    public int EntryCount { get; set; }

    private string _dir = null!;

    [IterationSetup]
    public void Setup()
    {
        _dir = BenchTempDir.Create("btree");
        Directory.CreateDirectory(_dir);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dir);
    }

    /// <summary>Sequential Int64 inserts — covers leaf no-split fast path and split path.</summary>
    [Benchmark(Description = "Int64 Insert (sequential)")]
    public long Int64InsertSequential()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        var idx = mgr.CreateInt64Index("bench");
        for (long i = 0; i < EntryCount; i++)
            idx.Insert(i, i);
        return idx.EntryCount;
    }

    /// <summary>Pseudo-random Int64 inserts — stresses non-tail leaf insertion (shift path).</summary>
    [Benchmark(Description = "Int64 Insert (random)")]
    public long Int64InsertRandom()
    {
        using var mgr = IndexManager.OpenStandalone(_dir);
        var idx = mgr.CreateInt64Index("bench");
        var rng = new Random(42);
        for (int i = 0; i < EntryCount; i++)
        {
            long k = (long)rng.Next() * 1_000_003L;
            idx.Insert(k, i);
        }
        return idx.EntryCount;
    }
}
