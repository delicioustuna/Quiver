using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="VertexByLabelScanOperator"/> scans Person vertices.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class VertexByLabelScanOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("vertexbylabel");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Scan_person()
    {
        using var op = new VertexByLabelScanOperator(_seed.PersonLabel);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
