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

    // VEC-12: SQLite backend は LabelNodeIndex sidecar を持たないため、HasFastLabelIndex は
    // 既定の false のまま。push-down 閾値は legacy 30% 単一値経路に倒れる。
    // ベクトル spec は in-memory vector store から引けるので forward する。
    public bool TryGetVectorIndexSpec(string indexName, out VectorIndexSpec spec)
        => _vectors.TryGetIndex(indexName, out spec);

    public VectorSearchCursor KnnSearch(string indexName, ReadOnlySpan<float> query, int k)
        => _vectors.KnnSearch(indexName, query, k);

    // VEC-8: SQLite backend も in-memory vector store と組まれている前提なので、
    // gather-then-score / batch 短絡を有効化する。
    public VectorSearchCursor KnnSearchFiltered(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        EntityCandidateSet candidates)
    {
        if (_vectors is InMemoryVectorStore inMem)
            return inMem.KnnSearchFiltered(indexName, query, k, candidates);
        return IGraphAccessMethods.KnnSearchFilteredOversample(this, indexName, query, k, candidates);
    }

    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k)
    {
        if (_vectors is InMemoryVectorStore inMem)
            return inMem.KnnSearchBatch(indexName, queries, k);
        return _vectors.KnnSearchBatch(indexName, queries, k);
    }

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
