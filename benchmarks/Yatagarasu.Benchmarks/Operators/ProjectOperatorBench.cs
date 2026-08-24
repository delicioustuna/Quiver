using BenchmarkDotNet.Attributes;
using Yatagarasu.Query.Physical;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary> sentinel: <see cref="ProjectOperator"/> doubles col 0 into a new column.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ProjectOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("project");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Project_double()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        var spec = new[] { new ProjectionSpec("doubled", new DoubleLongCompute()) };
        using var op = new ProjectOperator(src, spec);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
