using BenchmarkDotNet.Attributes;
using Quiver.Query.Physical;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="RelationshipEndpointOperator"/> reads Source endpoint.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RelationshipEndpointOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("relendpoint");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Endpoint_source()
    {
        var src = new RelArraySource(_seed.Relationships);
        using var op = new RelationshipEndpointOperator(src, 0, RelationshipEndpoint.Source);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
