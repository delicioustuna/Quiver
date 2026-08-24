using BenchmarkDotNet.Attributes;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary> sentinel: <see cref="BfsOperator"/> from a 20-vertex frontier, maxDepth=3.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BfsOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private VertexId[] _frontier = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("bfs");
        _frontier = new VertexId[20];
        Array.Copy(_seed.PersonVertices, _frontier, 20);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Bfs_depth3_outgoing()
    {
        var src = new VertexArraySource(_frontier);
        using var op = new BfsOperator(src, 0, Direction.Outgoing, null, maxDepth: 3);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
