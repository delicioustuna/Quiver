using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;
using static Quiver.Query.Physical.Tests.Support.OperatorCollect;

namespace Quiver.Query.Physical.Tests;

public class SortOperatorTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        var src = new FixedVertexListOperator();
        using var op = new SortOperator(src, sortColumn: 0);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Ascending_default_sorts_VertexId_by_value()
    {
        var src = new FixedVertexListOperator(
            new VertexId(5), new VertexId(1), new VertexId(3), new VertexId(2), new VertexId(4));
        using var op = new SortOperator(src, sortColumn: 0);
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void Descending_reverses_order()
    {
        var src = new FixedVertexListOperator(
            new VertexId(2), new VertexId(7), new VertexId(4));
        using var op = new SortOperator(src, sortColumn: 0, descending: true);
        op.Open(null!);
        Collect(op).Should().Equal(7, 4, 2);
    }

    [Fact]
    public void Stable_for_already_sorted_input()
    {
        var src = new FixedVertexListOperator(new VertexId(1), new VertexId(2), new VertexId(3));
        using var op = new SortOperator(src, sortColumn: 0);
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Single_element_input_returns_single()
    {
        var src = new FixedVertexListOperator(new VertexId(42));
        using var op = new SortOperator(src, sortColumn: 0);
        op.Open(null!);
        Collect(op).Should().Equal(42);
    }

    [Fact]
    public void Schema_passes_through_from_source()
    {
        var src = new FixedVertexListOperator();
        using var op = new SortOperator(src, sortColumn: 0);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Name.Should().Be("vertexId");
    }
}
