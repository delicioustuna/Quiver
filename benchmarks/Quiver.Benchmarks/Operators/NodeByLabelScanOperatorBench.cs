using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="NodeByLabelScanOperator"/> scans Person nodes.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class NodeByLabelScanOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("nodebylabel");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Scan_person()
    {
        using var op = new NodeByLabelScanOperator(_seed.PersonLabel);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
