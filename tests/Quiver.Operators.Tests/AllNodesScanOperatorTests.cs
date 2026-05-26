using FluentAssertions;
using Quiver.Operators;
using Quiver.Operators.Tests.Support;
using Xunit;

namespace Quiver.Operators.Tests;

public class AllNodesScanOperatorTests
{
    [Fact]
    public void Empty_database_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllNodesScanOperator());
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Scans_every_node_when_no_label_filter()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateNode("A");
            tx.CreateNode("B");
            tx.CreateNode("A");
        });
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllNodesScanOperator());
        result.Rows().Should().HaveCount(3);
        tx.Rollback();
    }

    [Fact]
    public void Filters_by_label_when_given()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateNode("A");
            tx.CreateNode("B");
            tx.CreateNode("A");
        });
        var labelA = fx.Db.Schema.GetOrCreateLabel("A");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllNodesScanOperator(labelA));
        result.Rows().Should().HaveCount(2);
        tx.Rollback();
    }

    [Fact]
    public void Label_miss_yields_empty()
    {
        using var fx = OperatorTestFixture.Open(tx => { tx.CreateNode("A"); });
        var labelB = fx.Db.Schema.GetOrCreateLabel("DoesNotExist");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllNodesScanOperator(labelB));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Schema_has_single_NodeId_column()
    {
        var op = new AllNodesScanOperator();
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.NodeId);
        op.Dispose();
    }
}
