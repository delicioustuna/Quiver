using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="FilterOperator"/> with a single even-id predicate.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FilterOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("filter");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Filter_even_id()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new FilterOperator(src, new EvenIdPredicate());
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
