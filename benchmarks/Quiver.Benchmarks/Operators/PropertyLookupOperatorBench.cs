using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="PropertyLookupOperator"/> reads <c>value</c> per row.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PropertyLookupOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("proplookup");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Lookup_value()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new PropertyLookupOperator(src, 0, _seed.ValueKey, "value");
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
