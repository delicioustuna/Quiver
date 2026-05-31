using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class ShortestPathOperatorTests
{
    [Fact]
    public void Empty_source_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var op = new ShortestPathOperator(
            new FixedNodeListOperator(), 0, 0, Direction.Both, null);
        // Empty PairSource — but we used FixedNodeListOperator which has only 1 column.
        // For empty input the operator must just yield no rows.
        using var result = tx.Execute(op);
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_hop_path_found()
    {
        NodeId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new ShortestPathOperator(
            new PairSourceOperator(a, b), 0, 1, Direction.Outgoing, null));
        result.Rows().Should().HaveCount(1);
        tx2.Rollback();
    }

    [Fact]
    public void No_path_yields_empty()
    {
        NodeId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X"); // disconnected
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new ShortestPathOperator(
            new PairSourceOperator(a, b), 0, 1, Direction.Outgoing, null));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Self_pair_returns_zero_length_path()
    {
        NodeId a = default;
        using var fx = OperatorTestFixture.Open(tx => { a = tx.CreateNode("X"); });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new ShortestPathOperator(
            new PairSourceOperator(a, a), 0, 1, Direction.Outgoing, null));
        result.Rows().Should().HaveCount(1);
        tx2.Rollback();
    }

    [Fact]
    public void MaxDistance_blocks_path_beyond_limit()
    {
        NodeId a = default, c = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            var b = tx.CreateNode("X");
            c = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "K");
            tx.CreateRelationship(b, c, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new ShortestPathOperator(
            new PairSourceOperator(a, c), 0, 1, Direction.Outgoing, null, maxDistance: 1));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}
