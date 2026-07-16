using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="VariableLengthExpandOperator"/> hops 1..3 outgoing.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class VariableLengthExpandOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private VertexId[] _frontier = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("varlen");
        _frontier = new VertexId[20];
        Array.Copy(_seed.PersonVertices, _frontier, 20);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int VarLen_1_to_3()
    {
        var src = new VertexArraySource(_frontier);
        using var op = new VariableLengthExpandOperator(src, 0, Direction.Outgoing, null, minHops: 1, maxHops: 3);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
