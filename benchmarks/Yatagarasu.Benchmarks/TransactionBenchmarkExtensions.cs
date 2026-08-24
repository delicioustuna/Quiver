namespace Yatagarasu.Benchmarks;

internal static class TransactionBenchmarkExtensions
{
    internal static TResult EditSchema<TResult>(
        this YatagarasuDatabase database,
        Func<ISchemaEditor, TResult> edit)
    {
        using var transaction = database.BeginWriteTransaction();
        TResult result = edit(transaction.EditSchema);
        transaction.Commit();
        return result;
    }

    internal static void EditSchema(
        this YatagarasuDatabase database,
        Action<ISchemaEditor> edit)
    {
        using var transaction = database.BeginWriteTransaction();
        edit(transaction.EditSchema);
        transaction.Commit();
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        long value,
        Yatagarasu.Core.VertexId vertex)
    {
        string key = ResolveIndexKey(transaction, indexName, IndexKind.Int64Equality);
        transaction.SetProperty(vertex, key, Yatagarasu.Storage.Records.PropertyValue.FromInt64(value));
    }

    internal static void SetIndexedProperty(
        this IWriteTransaction transaction,
        string indexName,
        string value,
        Yatagarasu.Core.VertexId vertex)
    {
        string key = ResolveIndexKey(transaction, indexName, IndexKind.StringEquality);
        transaction.SetProperty(vertex, key, Yatagarasu.Storage.Records.PropertyValue.FromString(value));
    }

    private static string ResolveIndexKey(
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
