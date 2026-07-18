using System.Text;
using System.Threading;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// バイナリバックエンドの <see cref="IGraphAccessMethods"/> 実装。リンクリストと隣接ブロックの
/// 選択を <see cref="BinaryExpandCursor"/> に委譲し、隣接 fast path が使えなかった頻度
/// (= インデックス構築時にブロックが無かったVertex、典型的には bulk load 後に作られたもの)
/// を診断用カウンタとして公開する。<see cref="IAdjacencySegmentStore.OpenCursor"/> が
/// ページチェーン全体を走査するため、cursor が走査途中で fast path を放棄することはない。
/// カウンタは「ブロックが全く無い」経路でのみ発火する。
/// </summary>
internal sealed class BinaryGraphAccessMethods : IGraphAccessMethods
{
    // BinaryExpandCursor から Interlocked 経由でアクセスされる。
    internal long FallbackCountInternal;

    private readonly IVectorStore _vectors;
    private readonly EdgeDeltaStore _edgeDeltas;
    // ラベル転置索引 (任意)。接続時はラベル付き ScanVertices/LabelScan が全件 Scan() から O(|L|) lookup に切り替わる。
    private LabelVertexIndex? _labelIndex;

    internal BinaryGraphAccessMethods(
        IVectorStore vectors,
        EdgeDeltaStore? edgeDeltas = null)
    {
        _vectors = vectors;
        _edgeDeltas = edgeDeltas ?? EdgeDeltaStore.Shared;
    }

    internal EdgeDeltaStore EdgeDeltas => _edgeDeltas;

    /// <summary>
    /// factory が VertexStore に attach した後の index を共有する。
    /// 接続前 (open 直後 / unit テスト) は <see cref="ScanByLabelSlow"/> にフォールバックする。
    /// </summary>
    internal void AttachLabelIndex(LabelVertexIndex labelIndex) => _labelIndex = labelIndex;

    public long AdjacencyFallbackCount => Interlocked.Read(ref FallbackCountInternal);

    /// <summary>
    /// <c>LabelVertexIndex</c> sidecar が接続されているときに <c>true</c>。
    /// factory が <see cref="AttachLabelIndex"/> を呼ぶ前 (open 直後 / 単体テスト) は <c>false</c>。
    /// </summary>
    public bool HasFastLabelIndex => _labelIndex is not null;

    /// <inheritdoc/>
    public bool TryGetVectorIndexSpec(string indexName, out VectorIndexSpec spec)
        => _vectors.TryGetIndex(indexName, out spec);

    /// <inheritdoc/>
    public bool TryGetVector(EntityKind kind, long entityId, string indexName, Span<float> destination)
        => _vectors.TryGetVector(kind, entityId, indexName, destination);

    public VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
        => _vectors.KnnSearch(indexName, query, k, options);

    // in-memory backend では gather-then-score / 単一 snapshot バッチで短絡。
    public VectorSearchCursor KnnSearchFiltered(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        EntityCandidateSet candidates,
        VectorSearchOptions? options = null)
    {
        if (_vectors is InMemoryVectorStore inMem)
            return inMem.KnnSearchFiltered(indexName, query, k, candidates, options);
        // 永続ストアも gather-then-score / scan+post-filter を直接持つ。
        if (_vectors is Storage.Records.PersistentVectorStore persistent)
            return persistent.KnnSearchFiltered(indexName, query, k, candidates, options);
        return IGraphAccessMethods.KnnSearchFilteredOversample(
            this, indexName, query, k, candidates, options);
    }

    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k,
        VectorSearchOptions? options = null)
    {
        if (_vectors is InMemoryVectorStore inMem)
            return inMem.KnnSearchBatch(indexName, queries, k, options);
        return _vectors.KnnSearchBatch(indexName, queries, k, options);
    }

    public IEnumerable<VertexId> ScanVertices(ITransaction tx, LabelId? label = null)
    {
        if (!label.HasValue) return tx.Vertices.Scan();
        // sidecar 接続済みなら O(|L|) lookup。factory が index を attach するまでは
        // 旧来の O(N) scan-and-filter にフォールバックし、スタンドアロンの
        // TransactionManager 構築 (backend なしのテスト等) でも動作する。
        if (_labelIndex is { } idx)
            return idx.Lookup(tx.Vertices, label.Value);
        return ScanByLabelSlow(tx, label.Value);
    }

    private static IEnumerable<VertexId> ScanByLabelSlow(ITransaction tx, LabelId label)
    {
        foreach (var id in tx.Vertices.Scan())
        {
            if (tx.Vertices.Read(id).Label == label)
                yield return id;
        }
    }

    public IEnumerable<VertexId> SeekVerticesByIndex(
        ITransaction tx,
        ScalarIndexDefinition definition,
        PropertyKeyId propertyKey,
        LabelId? scope,
        PropertyValue key)
        => IndexValueResolver.SeekVisibleVertexPropertyOwners(
            tx,
            definition,
            propertyKey,
            scope,
            in key);

    public ExpandCursor Expand(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter)
        => new BinaryExpandCursor(tx, source, direction, typeFilter, this);

    public double EstimateExpandCardinality(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter)
    {
        var materializer = new EntityIdentityMaterializer(tx.Vertices);
        if (!materializer.TryVertex(source, out source))
            return 0;

        // GraphStats 未接続のため、隣接ブロックがあれば安価な O(degree) プローブを使い、
        // なければチェーンを走査する。
        var adj = tx.AdjacencySegments;
        if (adj != null && adj.HasBlock(source))
        {
            var probe = new AdjacencyEntry[64];
            int baseCount = adj.ReadEdges(source, direction, typeFilter, probe);
            int deltaLimit = Math.Max(0, probe.Length - Math.Min(baseCount, probe.Length));
            int deltaCount = _edgeDeltas.Count(
                tx, source, direction, typeFilter, adj.BaseEdgeHwm, deltaLimit);
            int total = Math.Min(probe.Length, baseCount + deltaCount);
            return total;
        }

        double count = 0;
        var edgeId = tx.Vertices.Read(source).FirstEdgeId;
        while (edgeId.IsValid)
        {
            var edge = tx.Edges.Read(edgeId);
            bool typeOk = !typeFilter.HasValue || edge.Type == typeFilter.Value;
            bool dirOk = direction switch
            {
                Direction.Outgoing => edge.Source.Sequence == source.Sequence,
                Direction.Incoming => edge.Target.Sequence == source.Sequence,
                _ => true,
            };
            if (typeOk && dirOk) count++;
            edgeId = edge.Source.Sequence == source.Sequence ? edge.SourceNext : edge.TargetNext;
        }
        return count;
    }
}
