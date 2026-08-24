using BenchmarkDotNet.Attributes;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary> sentinel: <see cref="SortOperator"/> sorts 200 vertex ids ascending.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SortOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private VertexId[] _shuffled = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("sort");
        _shuffled = (VertexId[])_seed.PersonVertices.Clone();
        var rng = new Random(2026);
        for (int i = _shuffled.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_shuffled[i], _shuffled[j]) = (_shuffled[j], _shuffled[i]);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Sort_ascending()
    {
        var src = new VertexArraySource(_shuffled);
        using var op = new SortOperator(src, sortColumn: 0);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
