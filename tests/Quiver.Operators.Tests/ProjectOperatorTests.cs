using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Operators.Tests.Support;
using Xunit;
using static Quiver.Operators.Tests.Support.OperatorCollect;

namespace Quiver.Operators.Tests;

public class ProjectOperatorTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        var src = new FixedNodeListOperator();
        using var op = new ProjectOperator(src, [new ProjectionSpec("doubled", new DoubleValueCompute())]);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Computes_projection_per_row()
    {
        var src = new FixedNodeListOperator(new NodeId(10), new NodeId(20));
        using var op = new ProjectOperator(src, [new ProjectionSpec("doubled", new DoubleValueCompute())]);
        op.Open(null!);
        Collect(op).Should().Equal(20, 40);
    }

    [Fact]
    public void Schema_carries_output_column_names()
    {
        var src = new FixedNodeListOperator();
        using var op = new ProjectOperator(src, [
            new ProjectionSpec("a", new DoubleValueCompute()),
            new ProjectionSpec("b", new DoubleValueCompute()),
        ]);
        op.Schema.Columns.Should().HaveCount(2);
        op.Schema.Columns[0].Name.Should().Be("a");
        op.Schema.Columns[1].Name.Should().Be("b");
    }

    [Fact]
    public void Multiple_projections_compute_per_column()
    {
        var src = new FixedNodeListOperator(new NodeId(5));
        using var op = new ProjectOperator(src, [
            new ProjectionSpec("x", new DoubleValueCompute()),
            new ProjectionSpec("y", new DoubleValueCompute()),
        ]);
        op.Open(null!);
        op.MoveNext().Should().BeTrue();
        op.Current[0].LongValue.Should().Be(10);
        op.Current[1].LongValue.Should().Be(10);
        op.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Statistics_count_emitted_rows()
    {
        var src = new FixedNodeListOperator(new NodeId(1), new NodeId(2), new NodeId(3));
        using var op = new ProjectOperator(src, [new ProjectionSpec("d", new DoubleValueCompute())]);
        op.Open(null!);
        while (op.MoveNext()) { }
        op.Statistics.RowsProduced.Should().Be(3);
    }
}
