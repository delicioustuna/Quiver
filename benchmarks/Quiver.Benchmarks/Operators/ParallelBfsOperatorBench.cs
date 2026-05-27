using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;

namespace Quiver.Benchmarks.Operators;

/// <summary>
/// TS-6 sentinel for <c>ParallelBfsOperator</c>. The parallel operator is
/// <c>internal sealed</c>, so we drive it through <see cref="BfsOperator"/>
/// configured with <c>maxParallelism = -1</c> — that is the public entry
/// point and the path real callers actually exercise.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ParallelBfsOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private NodeId[] _frontier = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("pbfs");
        _frontier = new NodeId[20];
        Array.Copy(_seed.PersonNodes, _frontier, 20);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int ParallelBfs_depth3_via_BfsOperator()
    {
        var src = new NodeArraySource(_frontier);
        using var op = new BfsOperator(src, 0, Direction.Outgoing, null, maxDepth: 3, maxParallelism: -1);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
