using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="BfsOperator"/> from a 20-node frontier, maxDepth=3.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BfsOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private NodeId[] _frontier = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("bfs");
        _frontier = new NodeId[20];
        Array.Copy(_seed.PersonNodes, _frontier, 20);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Bfs_depth3_outgoing()
    {
        var src = new NodeArraySource(_frontier);
        using var op = new BfsOperator(src, 0, Direction.Outgoing, null, maxDepth: 3);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
