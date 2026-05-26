using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Operators.Tests.Support;
using Xunit;
using static Quiver.Operators.Tests.Support.OperatorCollect;

namespace Quiver.Operators.Tests;

public class PairWithConstantOperatorTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        var src = new FixedNodeListOperator();
        using var op = new PairWithConstantOperator(src, sourceColumn: 0, constant: new NodeId(99));
        op.Open(null!);
        op.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Pairs_every_input_with_constant()
    {
        var src = new FixedNodeListOperator(new NodeId(1), new NodeId(2), new NodeId(3));
        using var op = new PairWithConstantOperator(src, sourceColumn: 0, constant: new NodeId(99));
        op.Open(null!);
        var rows = CollectAllColumns(op);
        rows.Should().HaveCount(3);
        rows[0].Should().Equal(1L, 99L);
        rows[1].Should().Equal(2L, 99L);
        rows[2].Should().Equal(3L, 99L);
    }

    [Fact]
    public void Schema_has_source_and_target_columns()
    {
        var src = new FixedNodeListOperator();
        using var op = new PairWithConstantOperator(src, sourceColumn: 0, constant: new NodeId(1));
        op.Schema.Columns.Should().HaveCount(2);
        op.Schema.Columns[0].Name.Should().Be("source");
        op.Schema.Columns[1].Name.Should().Be("target");
    }

    [Fact]
    public void Constant_can_be_same_as_input_id()
    {
        var src = new FixedNodeListOperator(new NodeId(7));
        using var op = new PairWithConstantOperator(src, sourceColumn: 0, constant: new NodeId(7));
        op.Open(null!);
        var rows = CollectAllColumns(op);
        rows.Should().HaveCount(1);
        rows[0].Should().Equal(7L, 7L);
    }
}
