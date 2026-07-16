using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="FrontierLimitOperator"/> with rolling per-depth cap of 50.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FrontierLimitOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private (long, long)[] _rows = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("frontierlim");
        _rows = new (long, long)[OperatorBenchSeed.VertexCount];
        for (int i = 0; i < OperatorBenchSeed.VertexCount; i++)
            _rows[i] = (_seed.PersonVertices[i].Value, i % 4);
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int FrontierLimit_cap50()
    {
        var src = new DepthRowSource(_rows);
        using var op = new FrontierLimitOperator(src, depthColumn: 1, maxFrontierSize: 50);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
