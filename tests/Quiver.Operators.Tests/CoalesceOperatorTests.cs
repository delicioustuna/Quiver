using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;
using static Quiver.Query.Physical.Tests.Support.OperatorCollect;

namespace Quiver.Query.Physical.Tests;

public class CoalesceOperatorTests
{
    /// <summary>入力値を <paramref name="multiplier"/> 回出力する分岐。</summary>
    private sealed class ProbeBranch : IPhysicalOperator
    {
        private readonly CorrelatedInputOperator _probe;
        private readonly int _multiplier;
        private int _remaining;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];

        public ProbeBranch(CorrelatedInputOperator probe, int multiplier)
        { _probe = probe; _multiplier = multiplier; }

        public TupleSchema Schema { get; } = new([new ColumnDefinition("coalesce", TupleSlotType.VertexId)]);
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
        var src = new FixedVertexListOperator();
        var probe = new CorrelatedInputOperator();
        using var op = new CoalesceOperator(src, 0, [probe], [new ProbeBranch(probe, 1)]);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void First_emitting_branch_wins_and_later_branches_skipped()
    {
        var src = new FixedVertexListOperator(new VertexId(5));
        var pA = new CorrelatedInputOperator();
        var pB = new CorrelatedInputOperator();
        using var op = new CoalesceOperator(
            src, 0,
            [pA, pB],
            [new ProbeBranch(pA, 1), new ProbeBranch(pB, 1)]);
        op.Open(null!);
        Collect(op).Should().Equal(5); // only one branch contributes
    }

    [Fact]
    public void Empty_first_branch_falls_through_to_second()
    {
        var src = new FixedVertexListOperator(new VertexId(8));
        var pA = new CorrelatedInputOperator();
        var pB = new CorrelatedInputOperator();
        using var op = new CoalesceOperator(
            src, 0,
            [pA, pB],
            [new ProbeBranch(pA, 0), new ProbeBranch(pB, 1)]);
        op.Open(null!);
        Collect(op).Should().Equal(8);
    }

    [Fact]
    public void All_branches_empty_drops_input_row()
    {
        var src = new FixedVertexListOperator(new VertexId(3));
        var pA = new CorrelatedInputOperator();
        var pB = new CorrelatedInputOperator();
        using var op = new CoalesceOperator(
            src, 0,
            [pA, pB],
            [new ProbeBranch(pA, 0), new ProbeBranch(pB, 0)]);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Constructor_requires_matching_probe_branch_arity()
    {
        var src = new FixedVertexListOperator();
        var p = new CorrelatedInputOperator();
        Action act = () => new CoalesceOperator(src, 0, [p], [new ProbeBranch(p, 1), new ProbeBranch(p, 1)]);
        act.Should().Throw<ArgumentException>();
    }
}
