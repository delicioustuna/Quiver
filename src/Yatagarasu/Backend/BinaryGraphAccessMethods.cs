using System.Text;
using System.Threading;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu;

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

    private readonly IVectorDefinitionCatalog _vectorDefinitions;
    private readonly EdgeDeltaStore _edgeDeltas;
    private readonly LabelTokenStore _labels;
    private readonly EdgeTypeTokenStore _edgeTypes;
    private readonly NexusTypeTokenStore _nexusTypes;
    // ラベル転置索引 (任意)。接続時はラベル付き ScanVertices/LabelScan が全件 Scan() から O(|L|) lookup に切り替わる。
    private LabelVertexIndex? _labelIndex;

    internal BinaryGraphAccessMethods(
        IVectorDefinitionCatalog vectorDefinitions,
        LabelTokenStore labels,
        EdgeTypeTokenStore edgeTypes,
        NexusTypeTokenStore nexusTypes,
        EdgeDeltaStore? edgeDeltas = null)
    {
        _vectorDefinitions = vectorDefinitions;
        _labels = labels;
        _edgeTypes = edgeTypes;
        _nexusTypes = nexusTypes;
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
    public bool TryGetVectorIndex(
        string indexName,
        out VectorIndexDescriptor descriptor)
        => _vectorDefinitions.TryGet(indexName, out descriptor);

    /// <inheritdoc/>
    public bool TryGetVector(
        ITransaction transaction,
        EntityRef owner,
        string indexName,
        Span<float> destination)
    {
        if (!_vectorDefinitions.TryGet(
                indexName,
                out VectorIndexDescriptor descriptor)
            || descriptor.OwnerKind != owner.Kind
            || destination.Length < descriptor.Dimensions)
            return false;
        PropertyCursor properties = owner.Kind switch
        {
            EntityKind.Vertex => transaction.Vertices.EnumerateProperties(
                new VertexId(owner.Value),
                transaction.Properties),
            EntityKind.Edge => transaction.Edges.EnumerateProperties(
                new EdgeId(owner.Value),
                transaction.Properties),
            EntityKind.Nexus => transaction.Nexuses.EnumerateProperties(
                new NexusId(owner.Value),
                transaction.Properties),
            _ => default,
        };
        while (properties.MoveNext())
        {
            PropertyEntry property = properties.Current;
            if (property.KeyId != descriptor.TargetPropertyKeyId
                || property.Value.Type != PropertyValueType.FloatArray
                || property.Value.FloatArrayValue.Length != descriptor.Dimensions)
                continue;
            property.Value.FloatArrayValue.CopyTo(destination);
            return true;
        }
        return false;
    }

    public VectorSearchCursor KnnSearch(
        ITransaction transaction,
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
    {
        options = VectorSearchOptionsValidator.Normalize(options);
        if (!_vectorDefinitions.TryGet(
                indexName,
                out VectorIndexDescriptor descriptor))
            throw new VectorException($"Vector index '{indexName}' does not exist.");
        if (query.Length != descriptor.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {descriptor.Dimensions} dimensions, got {query.Length}.");
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k));

        var heap = new VectorKnnHeap(k);
        switch (descriptor.OwnerKind)
        {
            case EntityKind.Vertex:
                foreach (VertexId id in transaction.Vertices.Scan())
                {
                    using VertexReadHandle owner = transaction.Vertices.Read(id);
                    if (!owner.InUse
                        || descriptor.TargetScope is { } scope
                            && _labels.GetName(owner.Label) != scope)
                        continue;
                    OfferPrimaryVector(
                        transaction.Vertices.EnumerateProperties(id, transaction.Properties),
                        EntityRef.From(owner.Id),
                        descriptor,
                        query,
                        heap);
                }
                break;
            case EntityKind.Edge:
                foreach (EdgeId id in transaction.Edges.Scan())
                {
                    EdgeReadHandle owner = transaction.Edges.Read(id);
                    if (!owner.InUse
                        || descriptor.TargetScope is { } scope
                            && _edgeTypes.GetName(owner.Type) != scope)
                        continue;
                    OfferPrimaryVector(
                        transaction.Edges.EnumerateProperties(id, transaction.Properties),
                        EntityRef.From(owner.Id),
                        descriptor,
                        query,
                        heap);
                }
                break;
            case EntityKind.Nexus:
                foreach (NexusId id in transaction.Nexuses.Scan())
                {
                    using NexusReadHandle owner = transaction.Nexuses.Read(id);
                    if (!owner.InUse
                        || descriptor.TargetScope is { } scope
                            && _nexusTypes.GetName(owner.Type) != scope)
                        continue;
                    OfferPrimaryVector(
                        transaction.Nexuses.EnumerateProperties(id, transaction.Properties),
                        EntityRef.From(owner.Id),
                        descriptor,
                        query,
                        heap);
                }
                break;
        }
        return new MaterializedVectorSearchCursor(heap.ToSortedArray());
    }

    public VectorSearchCursor KnnSearchFiltered(
        ITransaction transaction,
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        IReadOnlySet<EntityRef> candidates,
        VectorSearchOptions? options = null)
    {
        if (candidates.Count == 0)
            return EmptyVectorSearchCursor.Instance;
        var hits = new List<VectorSearchResult>(k);
        using var cursor = KnnSearch(
            transaction,
            indexName,
            query,
            Math.Max(k, candidates.Count),
            options);
        while (cursor.MoveNext())
        {
            VectorSearchResult hit = cursor.Current;
            if (!candidates.Contains(hit.Owner))
                continue;
            hits.Add(hit);
            if (hits.Count == k)
                break;
        }
        return new MaterializedVectorSearchCursor(hits);
    }

    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        ITransaction transaction,
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k,
        VectorSearchOptions? options = null)
    {
        var result = new VectorSearchCursor[queries.Count];
        for (int i = 0; i < queries.Count; i++)
            result[i] = KnnSearch(transaction, indexName, queries[i].Span, k, options);
        return result;
    }

    private static void OfferPrimaryVector(
        PropertyCursor properties,
        EntityRef owner,
        VectorIndexDescriptor descriptor,
        ReadOnlySpan<float> query,
        VectorKnnHeap heap)
    {
        while (properties.MoveNext())
        {
            PropertyEntry property = properties.Current;
            if (property.KeyId != descriptor.TargetPropertyKeyId
                || property.Value.Type != PropertyValueType.FloatArray
                || property.Value.FloatArrayValue.Length != descriptor.Dimensions)
                continue;
            heap.Offer(new VectorSearchResult(
                owner,
                VectorMetrics.Score(
                    descriptor.Metric,
                    query,
                    property.Value.FloatArrayValue)));
            return;
        }
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
