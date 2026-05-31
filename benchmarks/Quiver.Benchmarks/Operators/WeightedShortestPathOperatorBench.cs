using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="WeightedShortestPathOperator"/> Dijkstra over 10 pairs.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class WeightedShortestPathOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private (NodeId, NodeId)[] _pairs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("wsp");
        _pairs = new (NodeId, NodeId)[10];
        for (int i = 0; i < 10; i++)
            _pairs[i] = (_seed.PersonNodes[i], _seed.PersonNodes[(i + 7) % OperatorBenchSeed.NodeCount]);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int WeightedShortestPath_dijkstra()
    {
        var src = new NodePairSource(_pairs);
        using var op = new WeightedShortestPathOperator(
            src, 0, 1, Direction.Outgoing, null,
            new PropertyChainWeightProvider(_seed.WeightKey));
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
