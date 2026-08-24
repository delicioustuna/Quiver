using FluentAssertions;
using Yatagarasu.Api.Internal;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Query.Physical.Tests.Support;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class PropertyExistsPredicateTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var key = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("name"));
        var pred = new PropertyExistsPredicate(0, key, mustExist: true);
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new FilterOperator(new FixedVertexListOperator(), pred));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Has_keeps_vertices_with_property()
    {
        VertexId withProp = default, without = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            withProp = tx.CreateVertex("X");
            tx.SetProperty(withProp, "name", PropertyValue.FromString("alice"));
            without = tx.CreateVertex("X");
        });
        var key = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("name"));
        var pred = new PropertyExistsPredicate(0, key, mustExist: true);
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new FilterOperator(
            new FixedVertexListOperator(withProp, without), pred));
        result.Rows().Should().HaveCount(1);
        result.Rows().Single().GetVertexId(0).Should().Be(withProp);
        tx2.Rollback();
    }

    [Fact]
    public void HasNot_keeps_vertices_without_property()
    {
        VertexId withProp = default, without = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            withProp = tx.CreateVertex("X");
            tx.SetProperty(withProp, "name", PropertyValue.FromString("bob"));
            without = tx.CreateVertex("X");
        });
        var key = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("name"));
        var pred = new PropertyExistsPredicate(0, key, mustExist: false);
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new FilterOperator(
            new FixedVertexListOperator(withProp, without), pred));
        result.Rows().Should().HaveCount(1);
        result.Rows().Single().GetVertexId(0).Should().Be(without);
        tx2.Rollback();
    }

    [Fact]
    public void Has_with_unknown_keyId_returns_no_rows()
    {
        VertexId n = default;
        using var fx = OperatorTestFixture.Open(tx => { n = tx.CreateVertex("X"); });
        // PropertyKeyId.Invalid で未登録キーを再現する。
        var pred = new PropertyExistsPredicate(0, default, mustExist: true);
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new FilterOperator(new FixedVertexListOperator(n), pred));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void HasNot_with_unknown_keyId_passes_everything()
    {
        VertexId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            b = tx.CreateVertex("X");
        });
        var pred = new PropertyExistsPredicate(0, default, mustExist: false);
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new FilterOperator(new FixedVertexListOperator(a, b), pred));
        result.Rows().Should().HaveCount(2);
        tx2.Rollback();
    }
}
