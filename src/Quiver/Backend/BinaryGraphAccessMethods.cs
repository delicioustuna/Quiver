using System.Text;
using System.Threading;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// バイナリバックエンドの <see cref="IGraphAccessMethods"/> 実装。リンクリストと隣接ブロックの
/// 選択を <see cref="BinaryExpandCursor"/> に委譲し、隣接 fast path が使えなかった頻度
/// (= インデックス構築時にブロックが無かったノード、典型的には bulk load 後に作られたもの)
/// を診断用カウンタとして公開する。<see cref="IAdjacencyBlockStore.OpenCursor"/> が
/// ページチェーン全体を走査するため、cursor が走査途中で fast path を放棄することはない。
/// カウンタは「ブロックが全く無い」経路でのみ発火する。
/// </summary>
internal sealed class BinaryGraphAccessMethods : IGraphAccessMethods
{
    // BinaryExpandCursor から Interlocked 経由でアクセスされる。
    internal long FallbackCountInternal;

    private readonly IVectorStore _vectors;
    // ラベル転置索引 (任意)。接続時はラベル付き ScanNodes/LabelScan が全件 Scan() から O(|L|) lookup に切り替わる。
    private LabelNodeIndex? _labelIndex;

    internal BinaryGraphAccessMethods(IVectorStore vectors)
    {
        _vectors = vectors;
    }

    /// <summary>
    /// factory が NodeStore に attach した後の index を共有する。
    /// 接続前 (open 直後 / unit テスト) は <see cref="ScanByLabelSlow"/> にフォールバックする。
    /// </summary>
    internal void AttachLabelIndex(LabelNodeIndex labelIndex) => _labelIndex = labelIndex;

    public long AdjacencyFallbackCount => Interlocked.Read(ref FallbackCountInternal);

    /// <summary>
    /// <c>LabelNodeIndex</c> sidecar が接続されているときに <c>true</c>。
    /// factory が <see cref="AttachLabelIndex"/> を呼ぶ前 (open 直後 / 単体テスト) は <c>false</c>。
    /// </summary>
    public bool HasFastLabelIndex => _labelIndex is not null;

    /// <inheritdoc/>
    public bool TryGetVectorIndexSpec(string indexName, out VectorIndexSpec spec)
        => _vectors.TryGetIndex(indexName, out spec);

    /// <inheritdoc/>
    public bool TryGetVector(EntityKind kind, long entityId, string indexName, Span<float> destination)
        => _vectors.TryGetVector(kind, entityId, indexName, destination);

    public VectorSearchCursor KnnSearch(string indexName, ReadOnlySpan<float> query, int k)
        => _vectors.KnnSearch(indexName, query, k);

    // in-memory backend では gather-then-score / 単一 snapshot バッチで短絡。
    public VectorSearchCursor KnnSearchFiltered(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        EntityCandidateSet candidates)
    {
        if (_vectors is InMemoryVectorStore inMem)
            return inMem.KnnSearchFiltered(indexName, query, k, candidates);
        // 永続ストアも gather-then-score / scan+post-filter を直接持つ。
        if (_vectors is Storage.Records.PersistentVectorStore persistent)
            return persistent.KnnSearchFiltered(indexName, query, k, candidates);
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
        // sidecar 接続済みなら O(|L|) lookup。factory が index を attach するまでは
        // 旧来の O(N) scan-and-filter にフォールバックし、スタンドアロンの
        // TransactionManager 構築 (backend なしのテスト等) でも動作する。
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
        // PropertyValue は ref struct なので yield を跨いで保持できない。
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
        // パック値を世代照合しつつ NodeId へ unpack し、slot 再利用の stale 参照を弾く。
        return IndexValueResolver.ResolveLiveNodeIds(ids, tx.Nodes);
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
        // GraphStats 未接続のため、隣接ブロックがあれば安価な O(degree) プローブを使い、
        // なければチェーンを走査する。
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
