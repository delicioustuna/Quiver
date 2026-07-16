using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="LabelNameLookupOperator"/> resolves label names per row.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class LabelNameLookupOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("labellookup");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Lookup_label_names()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new LabelNameLookupOperator(src, 0, lid => _seed.Db.Schema.GetLabelName(lid));
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
