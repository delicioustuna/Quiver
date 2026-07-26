using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;
using static Quiver.Query.Physical.Tests.Support.OperatorCollect;

namespace Quiver.Query.Physical.Tests;

public class FilterOperatorTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        var src = new FixedVertexListOperator();
        using var op = new FilterOperator(src, new AlwaysTruePredicate());
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Predicate_drops_non_matching_rows()
    {
        var src = new FixedVertexListOperator(
            new VertexId(1), new VertexId(2), new VertexId(3), new VertexId(4));
        using var op = new FilterOperator(src, new EvenVertexIdPredicate());
        op.Open(null!);
        Collect(op).Should().Equal(2, 4);
    }

    [Fact]
    public void All_false_predicate_yields_empty()
    {
        var src = new FixedVertexListOperator(new VertexId(1), new VertexId(2));
        using var op = new FilterOperator(src, new AlwaysFalsePredicate());
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void All_true_predicate_passes_everything_through()
    {
        var src = new FixedVertexListOperator(new VertexId(1), new VertexId(2), new VertexId(3));
        using var op = new FilterOperator(src, new AlwaysTruePredicate());
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Schema_matches_source_schema()
    {
        var src = new FixedVertexListOperator();
        using var op = new FilterOperator(src, new AlwaysTruePredicate());
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Name.Should().Be("vertexId");
    }

    [Fact]
    public void Statistics_count_only_passing_rows()
    {
        var src = new FixedVertexListOperator(new VertexId(1), new VertexId(2), new VertexId(3), new VertexId(4));
        using var op = new FilterOperator(src, new EvenVertexIdPredicate());
        op.Open(null!);
        while (op.MoveNext()) { }
        op.Statistics.RowsProduced.Should().Be(2);
    }
}
