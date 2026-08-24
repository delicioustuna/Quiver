using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Backend.Tests;

internal static class ScalarIndexTestExtensions
{
    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        string value,
        VertexId vertex)
    {
        EnsureDefinition(transaction, indexName, IndexKind.StringEquality);
        transaction.SetProperty(vertex, indexName, PropertyValue.FromString(value));
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        long value,
        VertexId vertex)
    {
        EnsureDefinition(transaction, indexName, IndexKind.Int64Equality);
        transaction.SetProperty(vertex, indexName, PropertyValue.FromInt64(value));
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        double value,
        VertexId vertex)
    {
        EnsureDefinition(transaction, indexName, IndexKind.DoubleEquality);
        transaction.SetProperty(vertex, indexName, PropertyValue.FromDouble(value));
    }

    private static void EnsureDefinition(
        IWriteTransaction transaction,
        string indexName,
        IndexKind kind)
    {
        if (transaction.EditSchema.ListIndexes().Any(x => x.Name == indexName))
            return;
        transaction.EditSchema.CreateIndex(new ScalarIndexDefinition(
            indexName,
            new PropertyTarget(PropertyOwnerKind.Vertex, indexName),
            kind));
    }
}
