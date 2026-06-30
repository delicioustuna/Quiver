using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;
using static Quiver.Query.Physical.Tests.Support.OperatorCollect;

namespace Quiver.Query.Physical.Tests;

public class OptionalOperatorTests
{
    /// <summary>入力値を指定回数出力する分岐。</summary>
    private sealed class ProbeBranch : IPhysicalOperator
    {
        private readonly CorrelatedInputOperator _probe;
        private readonly int _multiplier;
        private int _remaining;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];

        public ProbeBranch(CorrelatedInputOperator probe, int multiplier)
        { _probe = probe; _multiplier = multiplier; }

        public TupleSchema Schema { get; } = new([new ColumnDefinition("optional", TupleSlotType.NodeId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);

        public void Open(Quiver.Transactions.ITransaction tx)
        {
            _probe.Open(tx);
            _probe.MoveNext();
            _remaining = _multiplier;
        }

        public bool MoveNext()
        {
            if (_remaining <= 0) return false;
            _remaining--;
            _buffer[0] = _probe.Current[0];
            return true;
        }

        public void Dispose() { }
    }

    [Fact]
    public void Empty_source_returns_empty()
    {
        var src = new FixedNodeListOperator();
        var probe = new CorrelatedInputOperator();
        using var op = new OptionalOperator(src, 0, probe, new ProbeBranch(probe, 1));
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Branch_emits_passes_branch_rows()
    {
        var src = new FixedNodeListOperator(new NodeId(11));
        var probe = new CorrelatedInputOperator();
        using var op = new OptionalOperator(src, 0, probe, new ProbeBranch(probe, 2));
        op.Open(null!);
        Collect(op).Should().Equal(11, 11);
    }

    [Fact]
    public void Empty_branch_falls_through_with_source_value()
    {
        var src = new FixedNodeListOperator(new NodeId(99));
        var probe = new CorrelatedInputOperator();
        using var op = new OptionalOperator(src, 0, probe, new ProbeBranch(probe, 0));
        op.Open(null!);
        Collect(op).Should().Equal(99);
    }

    [Fact]
    public void Mixed_inputs_combine_branch_and_fallthrough()
    {
        var src = new FixedNodeListOperator(new NodeId(1), new NodeId(2), new NodeId(3));
        // 分岐は各行を一度ずつ出力するため、1、2、3 は変化しない。
        var probe = new CorrelatedInputOperator();
        using var op = new OptionalOperator(src, 0, probe, new ProbeBranch(probe, 1));
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 3);
    }
}
