using BenchmarkDotNet.Attributes;
using Quiver.Operators;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="LimitOperator"/> caps a 200-row stream to 50.</summary>
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
        var src = new NodeArraySource(_seed.PersonNodes);
        using var op = new LimitOperator(src, limit: 50, skip: 10);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
