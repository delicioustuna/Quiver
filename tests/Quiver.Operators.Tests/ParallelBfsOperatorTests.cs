using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Operators.Tests.Support;
using Xunit;

namespace Quiver.Operators.Tests;

public class ParallelBfsOperatorTests
{
    [Fact]
    public void Schema_has_startNode_endNode_depth()
    {
        var src = new FixedNodeListOperator();
        var op = new ParallelBfsOperator(src, 0, Direction.Both, null, maxDepth: 2);
        op.Schema.Columns.Select(c => c.Name).Should().Equal("startNode", "endNode", "depth");
        op.Dispose();
    }

    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginReadOnlyTransaction();
        using var result = tx.Execute(
            new ParallelBfsOperator(new FixedNodeListOperator(), 0, Direction.Both, null, 3));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Multiple_sources_each_emit_their_subtree()
    {
        NodeId a = default, c = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            var b = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "K");
            c = tx.CreateNode("X");
            var d = tx.CreateNode("X");
            tx.CreateRelationship(c, d, "K");
        });
        using var tx2 = fx.Db.BeginReadOnlyTransaction();
        using var result = tx2.Execute(
            new ParallelBfsOperator(new FixedNodeListOperator(a, c), 0, Direction.Outgoing, null, maxDepth: 2));
        result.Rows().Should().HaveCount(2);
        tx2.Rollback();
    }

    [Fact]
    public void Isolated_sources_return_empty()
    {
        NodeId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
        });
        using var tx2 = fx.Db.BeginReadOnlyTransaction();
        using var result = tx2.Execute(
            new ParallelBfsOperator(new FixedNodeListOperator(a, b), 0, Direction.Both, null, maxDepth: 3));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}
