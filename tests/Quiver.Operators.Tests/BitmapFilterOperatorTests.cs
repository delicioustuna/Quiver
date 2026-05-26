using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Operators.Tests.Support;
using Xunit;

namespace Quiver.Operators.Tests;

public class BitmapFilterOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new BitmapFilterOperator(
            new FixedNodeListOperator(),
            new IPredicate[] { new AlwaysTruePredicate() }));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_predicate_filters_like_FilterOperator()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new BitmapFilterOperator(
            new FixedNodeListOperator(new NodeId(1), new NodeId(2), new NodeId(3), new NodeId(4)),
            new IPredicate[] { new EvenNodeIdPredicate() }));
        result.Rows().Select(r => r.GetNodeId(0).Value).Should().Equal(2, 4);
        tx.Rollback();
    }

    [Fact]
    public void All_false_predicate_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new BitmapFilterOperator(
            new FixedNodeListOperator(new NodeId(1), new NodeId(2)),
            new IPredicate[] { new AlwaysFalsePredicate() }));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void All_true_predicate_passes_everything()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new BitmapFilterOperator(
            new FixedNodeListOperator(new NodeId(5), new NodeId(6)),
            new IPredicate[] { new AlwaysTruePredicate() }));
        result.Rows().Select(r => r.GetNodeId(0).Value).Should().Equal(5, 6);
        tx.Rollback();
    }
}
