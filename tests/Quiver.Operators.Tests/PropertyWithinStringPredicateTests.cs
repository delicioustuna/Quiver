using FluentAssertions;
using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class PropertyWithinStringPredicateTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var key = fx.Db.Schema.GetOrCreatePropertyKey("color");
        var pred = new PropertyWithinStringPredicate(0, key, new[] { "red" });
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new FilterOperator(new FixedVertexListOperator(), pred));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Within_keeps_only_listed_values()
    {
        VertexId red = default, blue = default, green = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            red = tx.CreateVertex("X");   tx.SetProperty(red, "color", PropertyValue.FromString("red"));
            blue = tx.CreateVertex("X");  tx.SetProperty(blue, "color", PropertyValue.FromString("blue"));
            green = tx.CreateVertex("X"); tx.SetProperty(green, "color", PropertyValue.FromString("green"));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("color");
        var pred = new PropertyWithinStringPredicate(0, key, new[] { "red", "green" });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(
            new FixedVertexListOperator(red, blue, green), pred));
        result.Rows().Should().HaveCount(2);
        result.Rows().Select(r => r.GetVertexId(0)).Should().BeEquivalentTo(new[] { red, green });
        tx2.Rollback();
    }

    [Fact]
    public void Empty_allowlist_drops_everything()
    {
        VertexId n = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            n = tx.CreateVertex("X");
            tx.SetProperty(n, "color", PropertyValue.FromString("red"));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("color");
        var pred = new PropertyWithinStringPredicate(0, key, Array.Empty<string>());
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(new FixedVertexListOperator(n), pred));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Missing_property_drops_row()
    {
        VertexId withProp = default, without = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            withProp = tx.CreateVertex("X");
            tx.SetProperty(withProp, "color", PropertyValue.FromString("red"));
            without = tx.CreateVertex("X");
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("color");
        var pred = new PropertyWithinStringPredicate(0, key, new[] { "red", "blue" });
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(
            new FixedVertexListOperator(withProp, without), pred));
        result.Rows().Should().HaveCount(1);
        result.Rows().Single().GetVertexId(0).Should().Be(withProp);
        tx2.Rollback();
    }
}
