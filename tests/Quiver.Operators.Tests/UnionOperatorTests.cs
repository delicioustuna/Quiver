using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;
using static Quiver.Query.Physical.Tests.Support.OperatorCollect;

namespace Quiver.Query.Physical.Tests;

public class UnionOperatorTests
{
    /// <summary>
    /// CorrelatedInputOperator を受け取り、束縛値を <see cref="_multiplier"/> 回出力する分岐。
    /// Union が入力行ごとに値を再束縛し、分岐の出力を連結することを検証する。
    /// </summary>
    private sealed class RepeatProbeBranch : IPhysicalOperator
    {
        private readonly CorrelatedInputOperator _probe;
        private readonly int _multiplier;
        private int _remaining;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];

        public RepeatProbeBranch(CorrelatedInputOperator probe, int multiplier)
        {
            _probe = probe;
            _multiplier = multiplier;
        }

        public TupleSchema Schema { get; } = new([new ColumnDefinition("union", TupleSlotType.VertexId)]);
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
        using var op = new UnionOperator(src, 0, [probe], [new RepeatProbeBranch(probe, 1)]);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Concatenates_branches_for_each_source_row()
    {
        var src = new FixedVertexListOperator(new VertexId(10), new VertexId(20));
        var probeA = new CorrelatedInputOperator();
        var probeB = new CorrelatedInputOperator();
        using var op = new UnionOperator(
            src, 0,
            [probeA, probeB],
            [new RepeatProbeBranch(probeA, 1), new RepeatProbeBranch(probeB, 1)]);
        op.Open(null!);
        // For each source row we should see it once from branch A and once from branch B.
        Collect(op).Should().Equal(10, 10, 20, 20);
    }

    [Fact]
    public void Mismatched_probe_branch_arity_throws()
    {
        var src = new FixedVertexListOperator();
        var probe = new CorrelatedInputOperator();
        Action act = () => new UnionOperator(
            src, 0,
            [probe],
            [new RepeatProbeBranch(probe, 1), new RepeatProbeBranch(probe, 1)]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Schema_is_single_VertexId_column()
    {
        var src = new FixedVertexListOperator();
        var probe = new CorrelatedInputOperator();
        using var op = new UnionOperator(src, 0, [probe], [new RepeatProbeBranch(probe, 1)]);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
    }
}
