using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Operators.Tests.Support;
using Xunit;

namespace Quiver.Operators.Tests;

public class RelationshipScanExpandOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new RelationshipScanExpandOperator(
            new FixedNodeListOperator(), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_outgoing_edge_emits_one_neighbor()
    {
        NodeId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            var b = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new RelationshipScanExpandOperator(
            new FixedNodeListOperator(a), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().HaveCount(1);
        tx2.Rollback();
    }

    [Fact]
    public void Disconnected_source_yields_empty()
    {
        NodeId a = default;
        using var fx = OperatorTestFixture.Open(tx => { a = tx.CreateNode("X"); });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new RelationshipScanExpandOperator(
            new FixedNodeListOperator(a), 0, Direction.Both, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}
