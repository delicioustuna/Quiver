using BenchmarkDotNet.Attributes;
using Quiver.Operators;

namespace Quiver.Benchmarks.Operators;

/// <summary>
/// TS-6 sentinel: <see cref="CorrelatedInputOperator"/> bind+emit cycle.
/// The operator is normally driven from inside another operator (Coalesce /
/// Optional / Union), so we measure the Bind/Open/MoveNext loop directly —
/// matching how the parent operators rearm it per outer row.
/// Open accepts <c>null</c> tx because CorrelatedInputOperator does not
/// touch the transaction (mirrors <c>CorrelatedInputOperatorTests</c>).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CorrelatedInputOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("correlated");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    [Benchmark]
    public int Bind_and_emit_200x()
    {
        var probe = new CorrelatedInputOperator();
        int n = 0;
        for (int i = 0; i < OperatorBenchSeed.NodeCount; i++)
        {
            probe.Bind(new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _seed.PersonNodes[i].Value });
            probe.Open(null!);
            while (probe.MoveNext()) n++;
        }
        return n;
    }
}
