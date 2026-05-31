using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;
using static Quiver.Query.Physical.Tests.Support.OperatorCollect;

namespace Quiver.Query.Physical.Tests;

public class PathDedupOperatorTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        var src = new FixedNodeListOperator();
        using var op = new PathDedupOperator(src, 0);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Removes_duplicate_keys_keeping_first_occurrence()
    {
        var src = new FixedNodeListOperator(
            new NodeId(1), new NodeId(2), new NodeId(1), new NodeId(3), new NodeId(2));
        using var op = new PathDedupOperator(src, 0);
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void All_unique_passes_through_unchanged()
    {
        var src = new FixedNodeListOperator(new NodeId(1), new NodeId(2), new NodeId(3));
        using var op = new PathDedupOperator(src, 0);
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void All_same_returns_single()
    {
        var src = new FixedNodeListOperator(
            new NodeId(7), new NodeId(7), new NodeId(7), new NodeId(7));
        using var op = new PathDedupOperator(src, 0);
        op.Open(null!);
        Collect(op).Should().Equal(7);
    }

    [Fact]
    public void Constructor_with_zero_keys_throws()
    {
        var src = new FixedNodeListOperator();
        Action act = () => new PathDedupOperator(src);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reopen_resets_seen_set()
    {
        var src = new FixedNodeListOperator(new NodeId(1), new NodeId(1));
        using var op = new PathDedupOperator(src, 0);
        op.Open(null!);
        Collect(op).Should().Equal(1);

        op.Open(null!);
        Collect(op).Should().Equal(1);
    }
}
