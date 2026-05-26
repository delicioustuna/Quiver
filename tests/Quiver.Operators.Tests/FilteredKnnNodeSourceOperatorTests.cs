using FluentAssertions;
using Quiver.Operators;
using Quiver.Operators.Tests.Support;
using Xunit;

namespace Quiver.Operators.Tests;

public class FilteredKnnNodeSourceOperatorTests
{
    [Fact]
    public void Constructor_rejects_empty_index_name()
    {
        Action act = () => new FilteredKnnNodeSourceOperator(
            new FixedNodeListOperator(), 0, "", new ReadOnlySpan<float>([1f]), 3);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_non_positive_k()
    {
        Action act = () => new FilteredKnnNodeSourceOperator(
            new FixedNodeListOperator(), 0, "idx", new ReadOnlySpan<float>([1f]), 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_rejects_null_source()
    {
        Action act = () => new FilteredKnnNodeSourceOperator(
            null!, 0, "idx", new ReadOnlySpan<float>([1f]), 3);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Schema_has_single_NodeId_column()
    {
        var op = new FilteredKnnNodeSourceOperator(
            new FixedNodeListOperator(), 0, "idx", new ReadOnlySpan<float>([1f, 2f]), 3);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.NodeId);
        op.Dispose();
    }
}
