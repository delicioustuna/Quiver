using FluentAssertions;
using Quiver.Client.Internal;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Operators.Tests.Support;
using Xunit;

namespace Quiver.Operators.Tests;

public class PropertyWithinStringPredicateTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var key = fx.Db.Schema.GetOrCreatePropertyKey("color");
        var pred = new PropertyWithinStringPredicate(0, key, new[] { "red" });
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new FilterOperator(new FixedNodeListOperator(), pred));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Within_keeps_only_listed_values()
    {
        NodeId red = default, blue = default, green = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            red = tx.CreateNode("X");   tx.SetProperty(red, "color", PropertyValue.FromString("red"));
            blue = tx.CreateNode("X");  tx.SetProperty(blue, "color", PropertyValue.FromString("blue"));
            green = tx.CreateNode("X"); tx.SetProperty(green, "color", PropertyValue.FromString("green"));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("color");
        var pred = new PropertyWithinStringPredicate(0, key, new[] { "red", "green" });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(
            new FixedNodeListOperator(red, blue, green), pred));
        result.Rows().Should().HaveCount(2);
        result.Rows().Select(r => r.GetNodeId(0)).Should().BeEquivalentTo(new[] { red, green });
        tx2.Rollback();
    }

    [Fact]
    public void Empty_allowlist_drops_everything()
    {
        NodeId n = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            n = tx.CreateNode("X");
            tx.SetProperty(n, "color", PropertyValue.FromString("red"));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("color");
        var pred = new PropertyWithinStringPredicate(0, key, Array.Empty<string>());
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(new FixedNodeListOperator(n), pred));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Missing_property_drops_row()
    {
        NodeId withProp = default, without = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            withProp = tx.CreateNode("X");
            tx.SetProperty(withProp, "color", PropertyValue.FromString("red"));
            without = tx.CreateNode("X");
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("color");
        var pred = new PropertyWithinStringPredicate(0, key, new[] { "red", "blue" });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(
            new FixedNodeListOperator(withProp, without), pred));
        result.Rows().Should().HaveCount(1);
        result.Rows().Single().GetNodeId(0).Should().Be(withProp);
        tx2.Rollback();
    }
}
