using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Query.Physical.Tests.Support;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class ParallelBfsOperatorTests
{
    [Fact]
    public void Schema_has_startVertex_endVertex_depth()
    {
        var src = new FixedVertexListOperator();
        var op = new ParallelBfsOperator(src, 0, Direction.Both, null, maxDepth: 2);
        op.Schema.Columns.Select(c => c.Name).Should().Equal("startVertex", "endVertex", "depth");
        op.Dispose();
    }

    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginReadTransaction();
        using var result = tx.Execute(
            new ParallelBfsOperator(new FixedVertexListOperator(), 0, Direction.Both, null, 3));
        result.Rows().Should().BeEmpty();
    }

    [Fact]
    public void Multiple_sources_each_emit_their_subtree()
    {
        VertexId a = default, c = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
            c = tx.CreateVertex("X");
            var d = tx.CreateVertex("X");
            tx.CreateEdge(c, d, "K");
        });
        using var tx2 = fx.Db.BeginReadTransaction();
        using var result = tx2.Execute(
            new ParallelBfsOperator(new FixedVertexListOperator(a, c), 0, Direction.Outgoing, null, maxDepth: 2));
        result.Rows().Should().HaveCount(2);
    }

    [Fact]
    public void Isolated_sources_return_empty()
    {
        VertexId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            b = tx.CreateVertex("X");
        });
        using var tx2 = fx.Db.BeginReadTransaction();
        using var result = tx2.Execute(
            new ParallelBfsOperator(new FixedVertexListOperator(a, b), 0, Direction.Both, null, maxDepth: 3));
        result.Rows().Should().BeEmpty();
    }
}
