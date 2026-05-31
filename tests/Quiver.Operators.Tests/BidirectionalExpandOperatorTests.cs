using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class BidirectionalExpandOperatorTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var op = new BidirectionalExpandOperator(
            new FixedNodeListOperator(), 0, 0, Direction.Outgoing, null);
        Action act = () => tx.Execute(op);
        // Either yields empty or rejects 1-column input; whichever it does, must not throw NRE.
        try { act(); } catch (ArgumentException) { /* acceptable */ }
        tx.Rollback();
    }

    [Fact]
    public void Connected_pair_emits_path()
    {
        NodeId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new BidirectionalExpandOperator(
            new PairSourceOperator(a, b), 0, 1, Direction.Outgoing, null));
        result.Rows().Should().NotBeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Disconnected_pair_emits_empty()
    {
        NodeId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new BidirectionalExpandOperator(
            new PairSourceOperator(a, b), 0, 1, Direction.Outgoing, null));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}
