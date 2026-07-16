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
        var src = new FixedVertexListOperator();
        var op = new ExpandOperator(src, 0, Direction.Both, null, ExpandOutputMode.NeighborOnly);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Name.Should().Be("neighbor");
        op.Dispose();
    }

    [Fact]
    public void Schema_Full_has_three_columns()
    {
        var src = new FixedVertexListOperator();
        var op = new ExpandOperator(src, 0, Direction.Both, null, ExpandOutputMode.Full);
        op.Schema.Columns.Should().HaveCount(3);
        op.Schema.Columns.Select(c => c.Name).Should().Equal("source", "edge", "neighbor");
        op.Dispose();
    }

    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(
            new ExpandOperator(new FixedVertexListOperator(), 0, Direction.Both, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_edge_yields_neighbor()
    {
        VertexId src = default, tgt = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateVertex("X");
            tgt = tx.CreateVertex("X");
            tx.CreateEdge(src, tgt, "KNOWS");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(src), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Select(r => r.GetVertexId(0)).Should().Equal(tgt);
        tx2.Rollback();
    }

    [Fact]
    public void Disconnected_vertex_yields_empty()
    {
        VertexId isolated = default;
        using var fx = OperatorTestFixture.Open(tx => { isolated = tx.CreateVertex("X"); });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(isolated), 0, Direction.Both, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Direction_filters_neighbors()
    {
        VertexId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            b = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K"); // a -> b
        });
        using var tx2 = fx.Db.BeginTransaction();
        // a への入力方向のEdgeは存在しない。
        using var inResult = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(a), 0, Direction.Incoming, null, ExpandOutputMode.NeighborOnly));
        inResult.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Type_filter_restricts_to_matching_edges()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            var c = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "KNOWS");
            tx.CreateEdge(a, c, "LIKES");
        });
        var knows = fx.Db.Schema.GetOrCreateEdgeType("KNOWS");
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(a), 0, Direction.Outgoing, knows, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().HaveCount(1);
        tx2.Rollback();
    }

    [Fact]
    public void NeighborAndEdge_mode_emits_edge_and_neighbor()
    {
        VertexId src = default, tgt = default;
        EdgeId edge = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateVertex("X");
            tgt = tx.CreateVertex("X");
            edge = tx.CreateEdge(src, tgt, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(src), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborAndEdge));
        result.Schema.Columns.Select(c => c.Name).Should().Equal("edge", "neighbor");
        var row = result.Rows().Single();
        row.GetInt64(0).Should().Be(edge.Value);
        row.GetVertexId(1).Should().Be(tgt);
        tx2.Rollback();
    }

    [Fact]
    public void NeighborAndWeight_schema_has_three_columns()
    {
        var src = new FixedVertexListOperator();
        var op = new ExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborAndWeight);
        op.Schema.Columns.Should().HaveCount(3);
        op.Schema.Columns.Select(c => c.Name).Should().Equal("edge", "neighbor", "weight");
        op.Dispose();
    }

    [Fact]
    public void NeighborAndWeight_emits_zero_when_no_payload_lane()
    {
        VertexId src = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateVertex("X");
            var tgt = tx.CreateVertex("X");
            tx.CreateEdge(src, tgt, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(src), 0, Direction.Outgoing, null, ExpandOutputMode.NeighborAndWeight));
        var row = result.Rows().Single();
        row.GetInt64(2).Should().Be(0); // documented zero fallback
        tx2.Rollback();
    }

    [Fact]
    public void Both_direction_includes_incoming_and_outgoing()
    {
        VertexId mid = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            var left = tx.CreateVertex("X");
            mid = tx.CreateVertex("X");
            var right = tx.CreateVertex("X");
            tx.CreateEdge(left, mid, "K");   // mid has incoming
            tx.CreateEdge(mid, right, "K");  // mid has outgoing
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(mid), 0, Direction.Both, null, ExpandOutputMode.NeighborOnly));
        result.Rows().Should().HaveCount(2);
        tx2.Rollback();
    }

    [Fact]
    public void CarryColumns_appends_upstream_slots_to_output()
    {
        // carryColumns は入力側の列を展開後の行へ引き継ぐ。
        VertexId src = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateVertex("X");
            var tgt = tx.CreateVertex("X");
            tx.CreateEdge(src, tgt, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(src), 0, Direction.Outgoing, null,
                ExpandOutputMode.NeighborOnly, carryColumns: new[] { 0 }));
        result.Schema.Columns.Should().HaveCount(2); // neighbor + carried column
        var row = result.Rows().Single();
        row.GetVertexId(1).Value.Should().Be(src.Value); // carried source vertexId
        tx2.Rollback();
    }

    [Fact]
    public void Full_mode_emits_source_edge_neighbor()
    {
        VertexId src = default, tgt = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            src = tx.CreateVertex("X");
            tgt = tx.CreateVertex("X");
            tx.CreateEdge(src, tgt, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(
            new ExpandOperator(new FixedVertexListOperator(src), 0, Direction.Outgoing, null, ExpandOutputMode.Full));
        result.Rows().Should().HaveCount(1);
        var row = result.Rows().Single();
        row.GetVertexId(0).Should().Be(src);
        row.GetVertexId(2).Should().Be(tgt);
        tx2.Rollback();
    }
}
