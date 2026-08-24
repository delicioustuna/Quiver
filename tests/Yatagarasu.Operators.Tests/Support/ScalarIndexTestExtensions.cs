using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Query.Physical.Tests.Support;

internal static class ScalarIndexTestExtensions
{
    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        long value,
        VertexId vertex)
    {
        string key = transaction.EditSchema.ListIndexes()
            .Single(x => x.Name == indexName)
            .Target.PropertyKey;
        transaction.SetProperty(vertex, key, PropertyValue.FromInt64(value));
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        double value,
        VertexId vertex)
    {
        string key = transaction.EditSchema.ListIndexes()
            .Single(x => x.Name == indexName)
            .Target.PropertyKey;
        transaction.SetProperty(vertex, key, PropertyValue.FromDouble(value));
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        string value,
        VertexId vertex)
    {
        string key = transaction.EditSchema.ListIndexes()
            .Single(x => x.Name == indexName)
            .Target.PropertyKey;
        transaction.SetProperty(vertex, key, PropertyValue.FromString(value));
    }
}
