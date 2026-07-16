using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="BidirectionalExpandOperator"/> over 10 (src,tgt) pairs.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BidirectionalExpandOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private (VertexId, VertexId)[] _pairs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("biexp");
        _pairs = new (VertexId, VertexId)[10];
        for (int i = 0; i < 10; i++)
            _pairs[i] = (_seed.PersonVertices[i], _seed.PersonVertices[(i + 7) % OperatorBenchSeed.VertexCount]);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Bidir_expand()
    {
        var src = new VertexPairSource(_pairs);
        using var op = new BidirectionalExpandOperator(src, 0, 1, Direction.Outgoing, null);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
