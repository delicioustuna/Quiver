using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="RelationshipScanExpandOperator"/> with a 200-node frontier.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RelationshipScanExpandOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("relscan");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Scan_expand_outgoing()
    {
        var src = new NodeArraySource(_seed.PersonNodes);
        using var op = new RelationshipScanExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
