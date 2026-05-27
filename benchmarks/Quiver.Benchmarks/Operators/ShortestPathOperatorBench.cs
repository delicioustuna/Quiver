using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="ShortestPathOperator"/> over 10 (src,tgt) pairs.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ShortestPathOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private (NodeId, NodeId)[] _pairs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("sp");
        _pairs = new (NodeId, NodeId)[10];
        for (int i = 0; i < 10; i++)
            _pairs[i] = (_seed.PersonNodes[i], _seed.PersonNodes[(i + 7) % OperatorBenchSeed.NodeCount]);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int ShortestPath_maxDistance_5()
    {
        var src = new NodePairSource(_pairs);
        using var op = new ShortestPathOperator(src, 0, 1, Direction.Outgoing, null, maxDistance: 5);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
