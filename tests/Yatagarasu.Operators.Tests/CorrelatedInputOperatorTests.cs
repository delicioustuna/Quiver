using FluentAssertions;
using Yatagarasu.Query.Physical;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class CorrelatedInputOperatorTests
{
    [Fact]
    public void Without_Open_yields_nothing()
    {
        using var op = new CorrelatedInputOperator();
        op.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void After_Open_emits_bound_value_once()
    {
        using var op = new CorrelatedInputOperator();
        op.Bind(new TupleSlot { Type = TupleSlotType.VertexId, LongValue = 42 });
        op.Open(null!);
        op.MoveNext().Should().BeTrue();
        op.Current[0].LongValue.Should().Be(42);
        op.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Re_Open_re_arms_for_next_emission()
    {
        using var op = new CorrelatedInputOperator();
        op.Bind(new TupleSlot { Type = TupleSlotType.VertexId, LongValue = 1 });
        op.Open(null!);
        op.MoveNext().Should().BeTrue();
        op.MoveNext().Should().BeFalse();

        op.Bind(new TupleSlot { Type = TupleSlotType.VertexId, LongValue = 2 });
        op.Open(null!);
        op.MoveNext().Should().BeTrue();
        op.Current[0].LongValue.Should().Be(2);
    }

    [Fact]
    public void Schema_has_single_VertexId_column()
    {
        using var op = new CorrelatedInputOperator();
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
    }
}
