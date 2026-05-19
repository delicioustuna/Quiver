using System.Text;
using System.Threading;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// Binary backend's <see cref="IGraphAccessMethods"/> implementation. Delegates
/// the linked-list vs adjacency-block choice to <see cref="BinaryExpandCursor"/>
/// and exposes a fallback counter so diagnostics can surface how often the
/// adjacency fast path was unavailable (i.e. the node had no adjacency block
/// at index-build time, typically because it was created after a bulk load).
/// PW-8: now that <see cref="IAdjacencyBlockStore.OpenCursor"/> walks the full
/// page chain, the cursor never abandons the fast path mid-iteration — so this
/// counter only fires on the "no block at all" path.
/// </summary>
internal sealed class BinaryGraphAccessMethods : IGraphAccessMethods
{
    // Accessed via Interlocked from BinaryExpandCursor.
    internal long FallbackCountInternal;

    private readonly IVectorStore _vectors;
    // VEC-11: optional label inverted index. When wired, label-filtered
    // ScanNodes/LabelScan switches from full Scan() to O(|L|) lookup.
    private LabelNodeIndex? _labelIndex;

    internal BinaryGraphAccessMethods(IVectorStore vectors)
    {
        _vectors = vectors;
    }

    /// <summary>
    /// VEC-11: factory が NodeStore に attach した後の index を共有する。
    /// 接続前 (open 直後 / unit テスト) は <see cref="ScanByLabelSlow"/> にフォールバックする。
    /// </summary>
    internal void AttachLabelIndex(LabelNodeIndex labelIndex) => _labelIndex = labelIndex;

    public long AdjacencyFallbackCount => Interlocked.Read(ref FallbackCountInternal);

    public VectorSearchCursor KnnSearch(string indexName, ReadOnlySpan<float> query, int k)
        => _vectors.KnnSearch(indexName, query, k);

    // VEC-8: in-memory backend では gather-then-score / 単一 snapshot バッチで短絡。
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
    {
        if (!label.HasValue) return tx.Nodes.Scan();
        // VEC-11: O(|L|) sidecar lookup when wired. Until the factory attaches
        // the index, fall back to the legacy O(N) scan-and-filter path so
        // standalone TransactionManager constructions (e.g. backend-less tests)
        // still work.
        if (_labelIndex is { } idx)
            return idx.Lookup(tx.Nodes, label.Value);
        return ScanByLabelSlow(tx, label.Value);
    }

    private static IEnumerable<NodeId> ScanByLabelSlow(ITransaction tx, LabelId label)
    {
        foreach (var id in tx.Nodes.Scan())
        {
            if (tx.Nodes.Read(id).Label == label)
                yield return id;
        }
    }

    public IEnumerable<NodeId> SeekNodesByIndex(ITransaction tx, string indexName, PropertyValue key)
    {
        // PropertyValue is a ref struct, so we cannot hold it across a yield.
        IEnumerable<long> ids = key.Type switch
        {
            PropertyValueType.Int32 or PropertyValueType.Int64 or PropertyValueType.Bool =>
                tx.Indexes.CreateInt64Index(indexName).SeekValues(key.Int64Value),
            PropertyValueType.Double =>
                tx.Indexes.CreateDoubleIndex(indexName).SeekValues(key.DoubleValue),
            PropertyValueType.String =>
                tx.Indexes.CreateStringIndex(indexName)
                    .SeekValues(Encoding.UTF8.GetString(key.Utf8StringValue)),
            _ => [],
        };
        return WrapNodeIds(ids);
    }

    private static IEnumerable<NodeId> WrapNodeIds(IEnumerable<long> ids)
    {
        foreach (var v in ids) yield return new NodeId(v);
    }

    public ExpandCursor Expand(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter)
        => new BinaryExpandCursor(tx, source, direction, typeFilter, this);

    public double EstimateExpandCardinality(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter)
    {
        // Without GraphStats wired in (BA-4), use a cheap O(degree) probe via
        // the adjacency block when present, otherwise walk the chain.
        var adj = tx.AdjacencyBlocks;
        if (adj != null && adj.HasBlock(source))
        {
            var probe = new AdjacencyEntry[64];
            int n = adj.ReadEdges(source, direction, typeFilter, probe);
            return n < probe.Length ? n : probe.Length;
        }

        double count = 0;
        var relId = tx.Nodes.Read(source).FirstRelationshipId;
        while (relId.IsValid)
        {
            var rel = tx.Relationships.Read(relId);
            bool typeOk = !typeFilter.HasValue || rel.Type == typeFilter.Value;
            bool dirOk = direction switch
            {
                Direction.Outgoing => rel.Source == source,
                Direction.Incoming => rel.Target == source,
                _ => true,
            };
            if (typeOk && dirOk) count++;
            relId = rel.Source == source ? rel.SourceNext : rel.TargetNext;
        }
        return count;
    }
}
