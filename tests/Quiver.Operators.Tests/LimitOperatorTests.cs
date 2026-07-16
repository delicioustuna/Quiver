using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;
using static Quiver.Query.Physical.Tests.Support.OperatorCollect;

namespace Quiver.Query.Physical.Tests;

public class LimitOperatorTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        var src = new FixedVertexListOperator();
        using var op = new LimitOperator(src, limit: 5);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Stops_at_limit()
    {
        var src = new FixedVertexListOperator(
            new VertexId(1), new VertexId(2), new VertexId(3), new VertexId(4), new VertexId(5));
        using var op = new LimitOperator(src, limit: 3);
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Skip_skips_initial_rows()
    {
        var src = new FixedVertexListOperator(
            new VertexId(1), new VertexId(2), new VertexId(3), new VertexId(4));
        using var op = new LimitOperator(src, limit: 2, skip: 1);
        op.Open(null!);
        Collect(op).Should().Equal(2, 3);
    }

    [Fact]
    public void Limit_larger_than_input_returns_all()
    {
        var src = new FixedVertexListOperator(new VertexId(7), new VertexId(8));
        using var op = new LimitOperator(src, limit: 100);
        op.Open(null!);
        Collect(op).Should().Equal(7, 8);
    }

    [Fact]
    public void Statistics_count_produced_rows()
    {
        var src = new FixedVertexListOperator(new VertexId(1), new VertexId(2), new VertexId(3));
        using var op = new LimitOperator(src, limit: 2);
        op.Open(null!);
        while (op.MoveNext()) { }
        op.Statistics.RowsProduced.Should().Be(2);
    }

    [Fact]
    public void Skip_exceeds_input_yields_empty()
    {
        var src = new FixedVertexListOperator(new VertexId(1), new VertexId(2));
        using var op = new LimitOperator(src, limit: 10, skip: 5);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Limit_zero_yields_empty()
    {
        var src = new FixedVertexListOperator(new VertexId(1), new VertexId(2));
        using var op = new LimitOperator(src, limit: 0);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }
}
