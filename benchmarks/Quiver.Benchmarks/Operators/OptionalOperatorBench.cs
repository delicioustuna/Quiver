using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="OptionalOperator"/> always emitting the probe value.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class OptionalOperatorBench
{
    private OperatorBenchSeed _seed = null!;

    [GlobalSetup]
    public void Setup() => _seed = new OperatorBenchSeed("optional");

    [GlobalCleanup]
    public void Cleanup() => _seed.Dispose();

    private sealed class EmitOnceBranch : IPhysicalOperator
    {
        private readonly CorrelatedInputOperator _probe;
        private bool _emitted;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        public EmitOnceBranch(CorrelatedInputOperator probe) => _probe = probe;
        public TupleSchema Schema { get; } = new([new ColumnDefinition("optional", TupleSlotType.VertexId)]);
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
    public int Optional_emit_each_row()
    {
        var src = new VertexArraySource(_seed.PersonVertices);
        var probe = new CorrelatedInputOperator();
        using var op = new OptionalOperator(src, 0, probe, new EmitOnceBranch(probe));
        return OperatorBenchDrain.Drain(op, _seed.ReadTx);
    }
}
