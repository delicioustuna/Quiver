using FluentAssertions;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class FilteredKnnVertexSourceOperatorTests
{
    [Fact]
    public void Constructor_rejects_empty_index_name()
    {
        Action act = () => new FilteredKnnVertexSourceOperator(
            new FixedVertexListOperator(), 0, "", new ReadOnlySpan<float>([1f]), 3);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_non_positive_k()
    {
        Action act = () => new FilteredKnnVertexSourceOperator(
            new FixedVertexListOperator(), 0, "idx", new ReadOnlySpan<float>([1f]), 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_rejects_null_source()
    {
        Action act = () => new FilteredKnnVertexSourceOperator(
            null!, 0, "idx", new ReadOnlySpan<float>([1f]), 3);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Schema_has_single_VertexId_column()
    {
        var op = new FilteredKnnVertexSourceOperator(
            new FixedVertexListOperator(), 0, "idx", new ReadOnlySpan<float>([1f, 2f]), 3);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
        op.Dispose();
    }
}
