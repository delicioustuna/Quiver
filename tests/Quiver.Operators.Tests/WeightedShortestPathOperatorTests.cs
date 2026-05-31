using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class WeightedShortestPathOperatorTests
{
    [Fact]
    public void Empty_source_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var key = fx.Db.Schema.GetOrCreatePropertyKey("w");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new WeightedShortestPathOperator(
            new FixedNodeListOperator(), 0, 0, Direction.Both, null, new PropertyChainWeightProvider(key)));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Lower_weight_path_preferred()
    {
        NodeId a = default, b = default, c = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
            c = tx.CreateNode("X");
            var direct = tx.CreateRelationship(a, b, "K");
            tx.SetProperty(direct, "w", PropertyValue.FromDouble(10.0));
            var detour1 = tx.CreateRelationship(a, c, "K");
            var detour2 = tx.CreateRelationship(c, b, "K");
            tx.SetProperty(detour1, "w", PropertyValue.FromDouble(1.0));
            tx.SetProperty(detour2, "w", PropertyValue.FromDouble(1.0));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("w");
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new WeightedShortestPathOperator(
            new PairSourceOperator(a, b), 0, 1, Direction.Outgoing, null, new PropertyChainWeightProvider(key)));
        result.Rows().Should().HaveCount(1);
        result.Rows().Single().GetDouble(2).Should().Be(2.0);
        tx2.Rollback();
    }

    [Fact]
    public void Self_pair_yields_zero_distance()
    {
        NodeId a = default;
        using var fx = OperatorTestFixture.Open(tx => { a = tx.CreateNode("X"); });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("w");
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new WeightedShortestPathOperator(
            new PairSourceOperator(a, a), 0, 1, Direction.Outgoing, null, new PropertyChainWeightProvider(key)));
        result.Rows().Single().GetDouble(2).Should().Be(0.0);
        tx2.Rollback();
    }

    [Fact]
    public void Negative_edge_weight_throws()
    {
        NodeId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
            var e = tx.CreateRelationship(a, b, "K");
            tx.SetProperty(e, "w", PropertyValue.FromDouble(-1.0));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("w");
        using var tx2 = fx.Db.BeginTransaction();
        var op = new WeightedShortestPathOperator(
            new PairSourceOperator(a, b), 0, 1, Direction.Outgoing, null, new PropertyChainWeightProvider(key));
        Action act = () => tx2.Execute(op);
        act.Should().Throw<InvalidOperationException>();
        tx2.Rollback();
    }

    [Fact]
    public void Missing_weight_property_uses_default_weight()
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
        var key = fx.Db.Schema.GetOrCreatePropertyKey("w");
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new WeightedShortestPathOperator(
            new PairSourceOperator(a, c), 0, 1, Direction.Outgoing, null, new PropertyChainWeightProvider(key)));
        result.Rows().Single().GetDouble(2).Should().Be(2.0); // 1.0 + 1.0 default
        tx2.Rollback();
    }
}
