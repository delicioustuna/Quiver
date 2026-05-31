using FluentAssertions;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class NodeByLabelScanOperatorTests
{
    [Fact]
    public void Empty_database_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var label = fx.Db.Schema.GetOrCreateLabel("Person");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new NodeByLabelScanOperator(label));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Returns_only_nodes_with_matching_label()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateNode("Person");
            tx.CreateNode("Person");
            tx.CreateNode("Movie");
            tx.CreateNode("Person");
        });
        var label = fx.Db.Schema.GetOrCreateLabel("Person");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new NodeByLabelScanOperator(label));
        result.Rows().Should().HaveCount(3);
        tx.Rollback();
    }

    [Fact]
    public void Unknown_label_returns_empty()
    {
        using var fx = OperatorTestFixture.Open(tx => { tx.CreateNode("Existing"); });
        var unknown = fx.Db.Schema.GetOrCreateLabel("NeverUsed");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new NodeByLabelScanOperator(unknown));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Schema_has_single_NodeId_column()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var label = fx.Db.Schema.GetOrCreateLabel("X");
        var op = new NodeByLabelScanOperator(label);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.NodeId);
        op.Dispose();
    }

    [Fact]
    public void Single_match_returns_one_row()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateNode("Solo");
            tx.CreateNode("Other");
            tx.CreateNode("Other");
        });
        var label = fx.Db.Schema.GetOrCreateLabel("Solo");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new NodeByLabelScanOperator(label));
        result.Rows().Should().HaveCount(1);
        tx.Rollback();
    }
}
