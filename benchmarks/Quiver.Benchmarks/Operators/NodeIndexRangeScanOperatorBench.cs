using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="NodeIndexRangeScanOperator"/> Int64 [50, 150) range.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class NodeIndexRangeScanOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("idxrange");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Range_50_to_150()
    {
        using var op = new NodeIndexRangeScanOperator(
            "idx_value",
            LiteralProvider.Int64(50), fromInclusive: true,
            LiteralProvider.Int64(150), toInclusive: false);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
