using BenchmarkDotNet.Attributes;
using Quiver.Operators;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="LabelNameLookupOperator"/> resolves label names per row.</summary>
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
        var src = new NodeArraySource(_seed.PersonNodes);
        using var op = new LabelNameLookupOperator(src, 0, lid => _seed.Db.Schema.GetLabelName(lid));
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
