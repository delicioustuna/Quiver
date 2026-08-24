using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Query.Physical.Tests.Support;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class BitmapFilterOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new BitmapFilterOperator(
            new FixedVertexListOperator(),
            new IPredicate[] { new AlwaysTruePredicate() }));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_predicate_filters_like_FilterOperator()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new BitmapFilterOperator(
            new FixedVertexListOperator(new VertexId(1), new VertexId(2), new VertexId(3), new VertexId(4)),
            new IPredicate[] { new EvenVertexIdPredicate() }));
        result.Rows().Select(r => r.GetVertexId(0).Value).Should().Equal(2, 4);
        tx.Rollback();
    }

    [Fact]
    public void All_false_predicate_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new BitmapFilterOperator(
            new FixedVertexListOperator(new VertexId(1), new VertexId(2)),
            new IPredicate[] { new AlwaysFalsePredicate() }));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void All_true_predicate_passes_everything()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new BitmapFilterOperator(
            new FixedVertexListOperator(new VertexId(5), new VertexId(6)),
            new IPredicate[] { new AlwaysTruePredicate() }));
        result.Rows().Select(r => r.GetVertexId(0).Value).Should().Equal(5, 6);
        tx.Rollback();
    }
}
