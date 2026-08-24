using FluentAssertions;
using Yatagarasu.Query.Physical;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class KnnVertexSourceOperatorTests
{
    [Fact]
    public void Constructor_rejects_empty_index_name()
    {
        Action act = () => new KnnVertexSourceOperator(
            "", new ReadOnlySpan<float>([1f, 2f]), k: 5);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_non_positive_k()
    {
        Action act = () => new KnnVertexSourceOperator(
            "idx", new ReadOnlySpan<float>([1f, 2f]), k: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Schema_has_single_VertexId_column()
    {
        var op = new KnnVertexSourceOperator("idx", new ReadOnlySpan<float>([0.1f, 0.2f]), k: 3);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
        op.Dispose();
    }

    [Fact]
    public void Negative_k_throws()
    {
        Action act = () => new KnnVertexSourceOperator(
            "idx", new ReadOnlySpan<float>([1f]), k: -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
