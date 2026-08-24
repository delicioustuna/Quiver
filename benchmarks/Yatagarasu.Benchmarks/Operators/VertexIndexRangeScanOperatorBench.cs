using BenchmarkDotNet.Attributes;
using Yatagarasu.Query.Physical;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary> sentinel: <see cref="VertexIndexRangeScanOperator"/> Int64 [50, 150) range.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class VertexIndexRangeScanOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("idxrange");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Range_50_to_150()
    {
        using var op = new VertexIndexRangeScanOperator(
            _seed.ValueIndex,
            _seed.ValueKey,
            _seed.PersonLabel,
            LiteralProvider.Int64(50),
            fromInclusive: true,
            LiteralProvider.Int64(150),
            toInclusive: false);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
