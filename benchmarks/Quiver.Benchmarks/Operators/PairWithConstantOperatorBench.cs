using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="PairWithConstantOperator"/> pairs every input with a fixed vertex.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PairWithConstantOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("pairconst");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Pair_with_first_person()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new PairWithConstantOperator(src, 0, _seed.PersonVertices[0]);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
