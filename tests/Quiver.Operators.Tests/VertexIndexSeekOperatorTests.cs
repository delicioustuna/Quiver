using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class VertexIndexSeekOperatorTests
{
    [Fact]
    public void Empty_index_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx", new PropertyTarget(PropertyOwnerKind.Vertex, "n", "Item"), IndexKind.Int64Equality)));
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(Seek(
            tx, "idx", LiteralProvider.Int64(42)));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Seek_returns_matching_vertex()
    {
        VertexId target = default;
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_v", new PropertyTarget(PropertyOwnerKind.Vertex, "v", "Item"), IndexKind.Int64Equality)));
        using (var tx = fx.Db.BeginWriteTransaction())
        {
            target = tx.CreateVertex("Item");
            tx.SetProperty(target, "v", PropertyValue.FromInt64(7));
            tx.SetIndexedProperty("idx_v", 7L, target);
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(Seek(
            tx2, "idx_v", LiteralProvider.Int64(7)));
        result.Rows().Single().GetVertexId(0).Should().Be(target);
        tx2.Rollback();
    }

    [Fact]
    public void Seek_miss_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_v", new PropertyTarget(PropertyOwnerKind.Vertex, "v", "Item"), IndexKind.Int64Equality)));
        using (var tx = fx.Db.BeginWriteTransaction())
        {
            var n = tx.CreateVertex("Item");
            tx.SetProperty(n, "v", PropertyValue.FromInt64(1));
            tx.SetIndexedProperty("idx_v", 1L, n);
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(Seek(
            tx2, "idx_v", LiteralProvider.Int64(999)));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void String_key_seek_works()
    {
        VertexId target = default;
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));
        using (var tx = fx.Db.BeginWriteTransaction())
        {
            target = tx.CreateVertex("Person");
            tx.SetProperty(target, "name", PropertyValue.FromString("Alice"));
            tx.SetIndexedProperty("idx_name", "Alice", target);
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(Seek(
            tx2, "idx_name", LiteralProvider.String("Alice")));
        result.Rows().Single().GetVertexId(0).Should().Be(target);
        tx2.Rollback();
    }

    private static VertexIndexSeekOperator Seek(
        IReadTransaction transaction,
        string indexName,
        ITupleProvider key)
    {
        ScalarIndexDefinition definition = transaction.Schema.ListIndexes()
            .Single(index => index.Name == indexName)
            .Definition;
        if (!transaction.Schema.TryGetPropertyKeyId(
            definition.Target.PropertyKey,
            out PropertyKeyId propertyKey))
            propertyKey = PropertyKeyId.Invalid;
        LabelId? scope = null;
        if (definition.Target.Scope is { } label)
        {
            if (!transaction.Schema.TryGetLabelId(label, out LabelId labelId))
                labelId = LabelId.Invalid;
            scope = labelId;
        }
        return new VertexIndexSeekOperator(
            definition,
            propertyKey,
            scope,
            key);
    }
}
