using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="NodeIndexSeekOperator"/> Int64 equality seek.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class NodeIndexSeekOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("idxseek");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Seek_int64_value_100()
    {
        using var op = new NodeIndexSeekOperator("idx_value", LiteralProvider.Int64(100));
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
