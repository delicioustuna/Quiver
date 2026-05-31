using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="AllNodesScanOperator"/> with no label filter.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class AllNodesScanOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("allnodes");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Scan()
        => OperatorBenchDrain.Drain(new AllNodesScanOperator(), _seed.ReadTx);

    [Benchmark]
    public int Scan_with_label_filter()
        => OperatorBenchDrain.Drain(new AllNodesScanOperator(_seed.PersonLabel), _seed.ReadTx);
}
