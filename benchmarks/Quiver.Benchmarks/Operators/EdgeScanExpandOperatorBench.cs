using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="EdgeScanExpandOperator"/> with a 200-vertex frontier.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class EdgeScanExpandOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("relscan");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Scan_expand_outgoing()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new EdgeScanExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
