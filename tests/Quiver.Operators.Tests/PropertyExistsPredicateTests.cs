using FluentAssertions;
using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class PropertyExistsPredicateTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var key = fx.Db.Schema.GetOrCreatePropertyKey("name");
        var pred = new PropertyExistsPredicate(0, key, mustExist: true);
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new FilterOperator(new FixedNodeListOperator(), pred));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Has_keeps_nodes_with_property()
    {
        NodeId withProp = default, without = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            withProp = tx.CreateNode("X");
            tx.SetProperty(withProp, "name", PropertyValue.FromString("alice"));
            without = tx.CreateNode("X");
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("name");
        var pred = new PropertyExistsPredicate(0, key, mustExist: true);
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(
            new FixedNodeListOperator(withProp, without), pred));
        result.Rows().Should().HaveCount(1);
        result.Rows().Single().GetNodeId(0).Should().Be(withProp);
        tx2.Rollback();
    }

    [Fact]
    public void HasNot_keeps_nodes_without_property()
    {
        NodeId withProp = default, without = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            withProp = tx.CreateNode("X");
            tx.SetProperty(withProp, "name", PropertyValue.FromString("bob"));
            without = tx.CreateNode("X");
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("name");
        var pred = new PropertyExistsPredicate(0, key, mustExist: false);
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(
            new FixedNodeListOperator(withProp, without), pred));
        result.Rows().Should().HaveCount(1);
        result.Rows().Single().GetNodeId(0).Should().Be(without);
        tx2.Rollback();
    }

    [Fact]
    public void Has_with_unknown_keyId_returns_no_rows()
    {
        NodeId n = default;
        using var fx = OperatorTestFixture.Open(tx => { n = tx.CreateNode("X"); });
        // PropertyKeyId.Invalid simulates "this key was never registered".
        var pred = new PropertyExistsPredicate(0, default, mustExist: true);
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(new FixedNodeListOperator(n), pred));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void HasNot_with_unknown_keyId_passes_everything()
    {
        NodeId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("X");
            b = tx.CreateNode("X");
        });
        var pred = new PropertyExistsPredicate(0, default, mustExist: false);
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new FilterOperator(new FixedNodeListOperator(a, b), pred));
        result.Rows().Should().HaveCount(2);
        tx2.Rollback();
    }
}
