using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class BfsOperatorTests
{
    [Fact]
    public void Schema_has_startVertex_endVertex_depth()
    {
        var src = new FixedVertexListOperator();
        var op = new BfsOperator(src, 0, Direction.Both, null, maxDepth: 2);
        op.Schema.Columns.Select(c => c.Name).Should().Equal("startVertex", "endVertex", "depth");
        op.Dispose();
    }

    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new BfsOperator(new FixedVertexListOperator(), 0, Direction.Both, null, 3));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Constructor_with_zero_depth_throws()
    {
        var src = new FixedVertexListOperator();
        Action act = () => new BfsOperator(src, 0, Direction.Both, null, maxDepth: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Single_chain_emits_depth_increasing()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            var c = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
            tx.CreateEdge(b, c, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new BfsOperator(new FixedVertexListOperator(a), 0, Direction.Outgoing, null, maxDepth: 3));
        var depths = result.Rows().Select(r => r.GetInt64(2)).ToList();
        depths.Should().Equal(1, 2);
        tx2.Rollback();
    }

    [Fact]
    public void Isolated_vertex_returns_empty()
    {
        VertexId iso = default;
        using var fx = OperatorTestFixture.Open(tx => { iso = tx.CreateVertex("X"); });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new BfsOperator(new FixedVertexListOperator(iso), 0, Direction.Both, null, 5));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void MaxDepth_clips_search()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            var c = tx.CreateVertex("X");
            var d = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
            tx.CreateEdge(b, c, "K");
            tx.CreateEdge(c, d, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new BfsOperator(new FixedVertexListOperator(a), 0, Direction.Outgoing, null, maxDepth: 2));
        result.Rows().Should().HaveCount(2); // b at depth 1, c at depth 2 — d at 3 is dropped
        tx2.Rollback();
    }

    [Fact]
    public void Cycle_does_not_loop_forever()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
            tx.CreateEdge(b, a, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new BfsOperator(new FixedVertexListOperator(a), 0, Direction.Outgoing, null, maxDepth: 5));
        result.Rows().Should().HaveCount(1); // b at depth 1, then a is already visited
        tx2.Rollback();
    }
}
