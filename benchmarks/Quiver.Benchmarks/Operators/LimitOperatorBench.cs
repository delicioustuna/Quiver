using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="LimitOperator"/> caps a 200-row stream to 50.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class LimitOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("limit");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Limit_50_skip_10()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new LimitOperator(src, limit: 50, skip: 10);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
