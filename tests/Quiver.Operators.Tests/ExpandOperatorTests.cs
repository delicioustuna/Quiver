using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class ExpandOperatorTests
{
    [Fact]
    public void Schema_NeighborOnly_has_one_column()
    {
        var src = new FixedNodeListOperator();
        var op = new ExpandOperator(src, 0, Direction.Both, null, ExpandOutputMode.NeighborOnly);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Name.Should().Be("neighbor");
        op.Dispose();
    }

    [Fact]
    public void Schema_Full_has_three_columns()
    {
        var src = new FixedNodeListOperator();
        var op = new ExpandOperator(src, 0, Direction.Both, null, ExpandOutputMode.Full);
        op.Schema.Columns.Should().HaveCount(3);
        op.Schema.Columns.Select(c => c.Name).Should().Equal("source", "rel", "neighbor");
        op.Dispose();
    }

    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(
            new ExpandOperator(new FixedNodeListOperator(), 0, Direction.Both, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_edge_yields_neighbor()
    {
        NodeId src = default, tgt = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateNode("X");
            tgt = tx.CreateNode("X");
            tx.CreateRelationship(src, tgt, "KNOWS");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(src), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Select(r => r.GetNodeId(0)).Should().Equal(tgt);
        tx2.Rollback();
    }

    [Fact]
    public void Disconnected_node_yields_empty()
    {
        NodeId isolated = default;
        using var fx = OperatorTestFixture.Open(tx => { isolated = tx.CreateNode("X"); });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(isolated), 0, Direction.Both, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Direction_filters_neighbors()
    {
        NodeId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "K"); // a -> b
        });
        using var tx2 = fx.Db.BeginTransaction();
        // a への入力方向のリレーションシップは存在しない。
        using var inResult = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(a), 0, Direction.Incoming, null, ExpandOutputMode.NeighborOnly));
        inResult.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Type_filter_restricts_to_matching_relationships()
    {
        NodeId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            var b = tx.CreateNode("X");
            var c = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "KNOWS");
            tx.CreateRelationship(a, c, "LIKES");
        });
        var knows = fx.Db.Schema.GetOrCreateRelationshipType("KNOWS");
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(a), 0, Direction.Outgoing, knows, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().HaveCount(1);
        tx2.Rollback();
    }

    [Fact]
    public void NeighborAndRel_mode_emits_rel_and_neighbor()
    {
        NodeId src = default, tgt = default;
        RelationshipId rel = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateNode("X");
            tgt = tx.CreateNode("X");
            rel = tx.CreateRelationship(src, tgt, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(src), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborAndRel));
        result.Schema.Columns.Select(c => c.Name).Should().Equal("rel", "neighbor");
        var row = result.Rows().Single();
        row.GetInt64(0).Should().Be(rel.Value);
        row.GetNodeId(1).Should().Be(tgt);
        tx2.Rollback();
    }

    [Fact]
    public void NeighborAndWeight_schema_has_three_columns()
    {
        var src = new FixedNodeListOperator();
        var op = new ExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborAndWeight);
        op.Schema.Columns.Should().HaveCount(3);
        op.Schema.Columns.Select(c => c.Name).Should().Equal("rel", "neighbor", "weight");
        op.Dispose();
    }

    [Fact]
    public void NeighborAndWeight_emits_zero_when_no_payload_lane()
    {
        NodeId src = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateNode("X");
            var tgt = tx.CreateNode("X");
            tx.CreateRelationship(src, tgt, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(src), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborAndWeight));
        var row = result.Rows().Single();
        row.GetInt64(2).Should().Be(0); // documented zero fallback
        tx2.Rollback();
    }

    [Fact]
    public void Both_direction_includes_incoming_and_outgoing()
    {
        NodeId mid = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            var left = tx.CreateNode("X");
            mid = tx.CreateNode("X");
            var right = tx.CreateNode("X");
            tx.CreateRelationship(left, mid, "K");   // mid has incoming
            tx.CreateRelationship(mid, right, "K");  // mid has outgoing
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(mid), 0, Direction.Both, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().HaveCount(2);
        tx2.Rollback();
    }

    [Fact]
    public void CarryColumns_appends_upstream_slots_to_output()
    {
        // carryColumns は入力側の列を展開後の行へ引き継ぐ。
        NodeId src = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateNode("X");
            var tgt = tx.CreateNode("X");
            tx.CreateRelationship(src, tgt, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(src), 0, Direction.Outgoing, null,
                ExpandOutputMode.NeighborOnly, carryColumns: new[] { 0 }));
        result.Schema.Columns.Should().HaveCount(2); // neighbor + carried column
        var row = result.Rows().Single();
        row.GetNodeId(1).Value.Should().Be(src.Value); // carried source nodeId
        tx2.Rollback();
    }

    [Fact]
    public void Full_mode_emits_source_rel_neighbor()
    {
        NodeId src = default, tgt = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateNode("X");
            tgt = tx.CreateNode("X");
            tx.CreateRelationship(src, tgt, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedNodeListOperator(src), 0, Direction.Outgoing, null, ExpandOutputMode.Full));
        result.Rows().Should().HaveCount(1);
        var row = result.Rows().Single();
        row.GetNodeId(0).Should().Be(src);
        row.GetNodeId(2).Should().Be(tgt);
        tx2.Rollback();
    }
}
