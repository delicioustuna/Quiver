using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Storage.Sqlite;

/// <summary>
/// BA-5 MVP stub. Operator routing through <c>tx.Access.Expand</c> isn't exercised
/// by the backend contract tests, but the interface must be satisfied so the
/// SQLite backend can be opened via <see cref="GraphDatabase"/>. Real access-method
/// fan-out for SQLite is deferred until the binary backend's access methods have
/// stabilised further.
/// </summary>
internal sealed class SqliteGraphAccessMethods : IGraphAccessMethods
{
    private readonly IVectorStore _vectors;

    internal SqliteGraphAccessMethods(IVectorStore vectors)
    {
        _vectors = vectors;
    }

    public long AdjacencyFallbackCount => 0;

    public VectorSearchCursor KnnSearch(string indexName, ReadOnlySpan<float> query, int k)
        => _vectors.KnnSearch(indexName, query, k);

    public IEnumerable<NodeId> ScanNodes(ITransaction tx, LabelId? label = null)
        => throw NotSupported(nameof(ScanNodes));

    public IEnumerable<NodeId> SeekNodesByIndex(ITransaction tx, string indexName, PropertyValue key)
        => throw NotSupported(nameof(SeekNodesByIndex));

    public ExpandCursor Expand(ITransaction tx, NodeId source, Direction direction, RelationshipTypeId? typeFilter)
        => throw NotSupported(nameof(Expand));

    public double EstimateExpandCardinality(ITransaction tx, NodeId source, Direction direction, RelationshipTypeId? typeFilter)
        => throw NotSupported(nameof(EstimateExpandCardinality));

    private static NotSupportedException NotSupported(string member) => new(
        $"SQLite backend (BA-5 MVP) does not implement IGraphAccessMethods.{member}. " +
        "Use IGraphTransaction.EnumerateRelationships / SeekIndex instead.");
}
