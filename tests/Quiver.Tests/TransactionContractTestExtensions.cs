using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Tests;

internal static class TransactionContractTestExtensions
{
    internal static TResult EditSchema<TResult>(
        this QuiverDatabase database,
        Func<ISchemaEditor, TResult> edit)
    {
        using var transaction = database.BeginWriteTransaction();
        TResult result = edit(transaction.EditSchema);
        transaction.Commit();
        return result;
    }

    internal static void EditSchema(
        this QuiverDatabase database,
        Action<ISchemaEditor> edit)
    {
        using var transaction = database.BeginWriteTransaction();
        edit(transaction.EditSchema);
        transaction.Commit();
    }

    internal static void EnsureIndexes<T>(this QuiverDatabase database)
        where T : IGraphVertex<T>, IGraphVertexSchema<T>
    {
        database.EditSchema(schema => schema.EnsureIndexes<T>());
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        string value,
        VertexId vertex)
    {
        string propertyKey = ResolvePropertyKey(
            transaction,
            indexName,
            IndexKind.StringEquality);
        transaction.SetProperty(
            vertex,
            propertyKey,
            PropertyValue.FromString(value));
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        long value,
        VertexId vertex)
    {
        string propertyKey = ResolvePropertyKey(
            transaction,
            indexName,
            IndexKind.Int64Equality);
        transaction.SetProperty(
            vertex,
            propertyKey,
            PropertyValue.FromInt64(value));
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        double value,
        VertexId vertex)
    {
        string propertyKey = ResolvePropertyKey(
            transaction,
            indexName,
            IndexKind.DoubleEquality);
        transaction.SetProperty(
            vertex,
            propertyKey,
            PropertyValue.FromDouble(value));
    }

    private static string ResolvePropertyKey(
        IWriteTransaction transaction,
        string indexName,
        IndexKind kind)
    {
        IndexInfo? existing = transaction.EditSchema.ListIndexes()
            .FirstOrDefault(x => x.Name == indexName);
        if (existing is not null)
            return existing.Target.PropertyKey;

        transaction.EditSchema.CreateIndex(new ScalarIndexDefinition(
            indexName,
            new PropertyTarget(PropertyOwnerKind.Vertex, indexName),
            kind));
        return indexName;
    }
}
