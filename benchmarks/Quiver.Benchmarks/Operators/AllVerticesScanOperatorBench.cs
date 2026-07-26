using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="AllVerticesScanOperator"/> with no label filter.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class AllVerticesScanOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("allvertices");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Scan()
        => OperatorBenchDrain.Drain(new AllVerticesScanOperator(), _seed.ReadTx);

    [Benchmark]
    public int Scan_with_label_filter()
        => OperatorBenchDrain.Drain(new AllVerticesScanOperator(_seed.PersonLabel), _seed.ReadTx);
}
