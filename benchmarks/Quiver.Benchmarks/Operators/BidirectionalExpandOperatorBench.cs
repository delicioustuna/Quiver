using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="BidirectionalExpandOperator"/> over 10 (src,tgt) pairs.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BidirectionalExpandOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private (NodeId, NodeId)[] _pairs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("biexp");
        _pairs = new (NodeId, NodeId)[10];
        for (int i = 0; i < 10; i++)
            _pairs[i] = (_seed.PersonNodes[i], _seed.PersonNodes[(i + 7) % OperatorBenchSeed.NodeCount]);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Bidir_expand()
    {
        var src = new NodePairSource(_pairs);
        using var op = new BidirectionalExpandOperator(src, 0, 1, Direction.Outgoing, null);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
