using FluentAssertions;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;
using static Quiver.Query.Physical.Tests.Support.OperatorCollect;

namespace Quiver.Query.Physical.Tests;

public class FrontierLimitOperatorTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        var src = new DepthTaggedSourceOperator();
        using var op = new FrontierLimitOperator(src, depthColumn: 1, maxFrontierSize: 4);
        op.Open(null!);
        Collect(op).Should().BeEmpty();
    }

    [Fact]
    public void Drops_excess_rows_within_same_depth()
    {
        var src = new DepthTaggedSourceOperator(
            (1, 0), (2, 0), (3, 0), (4, 0), (5, 0));
        using var op = new FrontierLimitOperator(src, depthColumn: 1, maxFrontierSize: 3);
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Counter_resets_on_new_depth()
    {
        var src = new DepthTaggedSourceOperator(
            (1, 0), (2, 0), (3, 0),     // depth 0 — all kept (limit 2 → keep 1,2)
            (4, 1), (5, 1));             // depth 1 — keep both (limit 2)
        using var op = new FrontierLimitOperator(src, depthColumn: 1, maxFrontierSize: 2);
        op.Open(null!);
        Collect(op).Should().Equal(1, 2, 4, 5);
    }

    [Fact]
    public void Limit_one_keeps_first_of_each_depth()
    {
        var src = new DepthTaggedSourceOperator(
            (1, 0), (2, 0),
            (3, 1), (4, 1), (5, 1));
        using var op = new FrontierLimitOperator(src, depthColumn: 1, maxFrontierSize: 1);
        op.Open(null!);
        Collect(op).Should().Equal(1, 3);
    }

    [Fact]
    public void Constructor_with_zero_limit_throws()
    {
        var src = new DepthTaggedSourceOperator();
        Action act = () => new FrontierLimitOperator(src, depthColumn: 1, maxFrontierSize: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
