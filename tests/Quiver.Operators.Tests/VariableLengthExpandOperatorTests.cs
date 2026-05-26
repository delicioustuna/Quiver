using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Operators.Tests.Support;
using Xunit;

namespace Quiver.Operators.Tests;

public class VariableLengthExpandOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new VariableLengthExpandOperator(
            new FixedNodeListOperator(), 0, Direction.Outgoing, null, minHops: 1, maxHops: 3));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Chain_emits_all_within_hop_window()
    {
        NodeId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            var b = tx.CreateNode("X");
            var c = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "K");
            tx.CreateRelationship(b, c, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new VariableLengthExpandOperator(
            new FixedNodeListOperator(a), 0, Direction.Outgoing, null, minHops: 1, maxHops: 2));
        result.Rows().Should().HaveCount(2); // b and c
        tx2.Rollback();
    }

    [Fact]
    public void MinHops_filters_short_paths()
    {
        NodeId a = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            var b = tx.CreateNode("X");
            var c = tx.CreateNode("X");
            tx.CreateRelationship(a, b, "K");
            tx.CreateRelationship(b, c, "K");
        });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new VariableLengthExpandOperator(
            new FixedNodeListOperator(a), 0, Direction.Outgoing, null, minHops: 2, maxHops: 2));
        result.Rows().Should().HaveCount(1); // only c
        tx2.Rollback();
    }

    [Fact]
    public void Isolated_node_returns_empty()
    {
        NodeId iso = default;
        using var fx = OperatorTestFixture.Open(tx => { iso = tx.CreateNode("X"); });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new VariableLengthExpandOperator(
            new FixedNodeListOperator(iso), 0, Direction.Both, null, minHops: 1, maxHops: 3));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}
