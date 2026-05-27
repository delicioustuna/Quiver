using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Operators;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="PathDedupOperator"/> drops duplicates by node id column.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PathDedupOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private NodeId[] _withDupes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("dedup");
        _withDupes = new NodeId[OperatorBenchSeed.NodeCount];
        for (int i = 0; i < OperatorBenchSeed.NodeCount; i++)
            _withDupes[i] = _seed.PersonNodes[i % 100];
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Dedup_column0()
    {
        var src = new NodeArraySource(_withDupes);
        using var op = new PathDedupOperator(src, 0);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
