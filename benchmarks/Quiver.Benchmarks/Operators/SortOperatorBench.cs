using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Operators;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="SortOperator"/> sorts 200 node ids ascending.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SortOperatorBench
{
    private OperatorBenchSeed _seed = null!;
    private NodeId[] _shuffled = null!;

    [GlobalSetup]
    public void Setup()
    {
        _seed = new OperatorBenchSeed("sort");
        _shuffled = (NodeId[])_seed.PersonNodes.Clone();
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
        var src = new NodeArraySource(_shuffled);
        using var op = new SortOperator(src, sortColumn: 0);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
