using BenchmarkDotNet.Attributes;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary> sentinel: <see cref="WeightedShortestPathOperator"/> Dijkstra over 10 pairs.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class WeightedShortestPathOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private (VertexId, VertexId)[] _pairs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("wsp");
        _pairs = new (VertexId, VertexId)[10];
        for (int i = 0; i < 10; i++)
            _pairs[i] = (_seed.PersonVertices[i], _seed.PersonVertices[(i + 7) % OperatorBenchSeed.VertexCount]);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int WeightedShortestPath_dijkstra()
    {
        var src = new VertexPairSource(_pairs);
        using var op = new WeightedShortestPathOperator(
            src, 0, 1, Direction.Outgoing, null,
            new PropertyChainWeightProvider(_seed.WeightKey));
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
