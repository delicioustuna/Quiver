using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="BitmapFilterOperator"/> with a single even-id predicate.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BitmapFilterOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("bitmap");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int BitmapFilter_even()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new BitmapFilterOperator(src, new IPredicate[] { new EvenIdPredicate() });
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
