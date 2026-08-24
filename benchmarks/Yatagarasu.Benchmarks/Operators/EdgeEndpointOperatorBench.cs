using BenchmarkDotNet.Attributes;
using Yatagarasu.Query.Physical;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary> sentinel: <see cref="EdgeEndpointOperator"/> reads Source endpoint.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class EdgeEndpointOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("relendpoint");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Endpoint_source()
    {
        var src = new RelArraySource(_seed.Edges);
        using var op = new EdgeEndpointOperator(src, 0, EdgeEndpoint.Source);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
