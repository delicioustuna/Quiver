using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="CoalesceOperator"/> with 2 branches per row.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CoalesceOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("coalesce");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    private sealed class EmitOnceBranch : IPhysicalOperator
    {
        private readonly CorrelatedInputOperator _probe;
        private bool _emitted;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        public EmitOnceBranch(CorrelatedInputOperator probe) => _probe = probe;
        public TupleSchema Schema { get; } = new([new ColumnDefinition("coalesce", TupleSlotType.NodeId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(ITransaction tx) { _probe.Open(tx); _probe.MoveNext(); _emitted = false; }
        public bool MoveNext()
        {
            if (_emitted) return false;
            _buffer[0] = _probe.Current[0];
            _emitted = true;
            return true;
        }
        public void Dispose() { }
    }

    [Benchmark]
    public int Coalesce_first_branch_wins()
    {
        var src = new NodeArraySource(_seed.PersonNodes);
        var pA = new CorrelatedInputOperator();
        var pB = new CorrelatedInputOperator();
        using var op = new CoalesceOperator(src, 0,
            [pA, pB],
            [new EmitOnceBranch(pA), new EmitOnceBranch(pB)]);
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
