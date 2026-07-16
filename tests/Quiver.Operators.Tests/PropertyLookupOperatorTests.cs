using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class PropertyLookupOperatorTests
{
    [Fact]
    public void Empty_source_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        var key = fx.Db.Schema.GetOrCreatePropertyKey("name");
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new PropertyLookupOperator(
            new FixedVertexListOperator(), 0, key, "name"));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Reads_string_property()
    {
        VertexId n = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            n = tx.CreateVertex("X");
            tx.SetProperty(n, "name", PropertyValue.FromString("Alice"));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("name");
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new PropertyLookupOperator(
            new FixedVertexListOperator(n), 0, key, "name"));
        result.Rows().Single().GetString(1).Should().Be("Alice");
        tx2.Rollback();
    }

    [Fact]
    public void Missing_property_yields_null_slot()
    {
        VertexId n = default;
        using var fx = OperatorTestFixture.Open(tx => { n = tx.CreateVertex("X"); });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("missing");
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new PropertyLookupOperator(
            new FixedVertexListOperator(n), 0, key, "missing"));
        result.Rows().Single().GetString(1).Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Reads_int_property()
    {
        VertexId n = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            n = tx.CreateVertex("X");
            tx.SetProperty(n, "age", PropertyValue.FromInt64(42));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("age");
        using var tx2 = fx.Db.BeginTransaction();
        using var result = tx2.Execute(new PropertyLookupOperator(
            new FixedVertexListOperator(n), 0, key, "age"));
        result.Rows().Single().GetInt64(1).Should().Be(42);
        tx2.Rollback();
    }

    [Fact]
    public void Expected_types_filter_rejects_wrong_type()
    {
        VertexId n = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            n = tx.CreateVertex("X");
            tx.SetProperty(n, "name", PropertyValue.FromString("Bob"));
        });
        var key = fx.Db.Schema.GetOrCreatePropertyKey("name");
        using var tx2 = fx.Db.BeginTransaction();
        // 文字列値に対して Int だけを要求するため、出力は null のままになる。
        using var result = tx2.Execute(new PropertyLookupOperator(
            new FixedVertexListOperator(n), 0, key, "name",
            PropertyTypeFlags.Int64));
        result.Rows().Single().GetString(1).Should().BeEmpty();
        tx2.Rollback();
    }
}
