using BenchmarkDotNet.Attributes;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary> sentinel: <see cref="PathDedupOperator"/> drops duplicates by vertex id column.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PathDedupOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private VertexId[] _withDupes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("dedup");
        _withDupes = new VertexId[OperatorBenchSeed.VertexCount];
        for (int i = 0; i < OperatorBenchSeed.VertexCount; i++)
            _withDupes[i] = _seed.PersonVertices[i % 100];
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Dedup_column0()
    {
        var src = new VertexArraySource(_withDupes);
        using var op = new PathDedupOperator(src, 0);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
