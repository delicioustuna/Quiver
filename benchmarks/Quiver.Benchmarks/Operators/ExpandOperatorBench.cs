using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="ExpandOperator"/> 1-hop NeighborOnly outgoing.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ExpandOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("expand");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Expand_outgoing_neighbor_only()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new ExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }

    [Benchmark]
    public int Expand_outgoing_full()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        using var op = new ExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.Full);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
