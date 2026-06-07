using System.Buffers;
using System.Threading;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// ARCH-6: container テナントへベクトル payload を永続化する <see cref="IVectorStore"/>。
/// <see cref="InMemoryVectorStore"/> を置き換え、再起動を跨いで KNN を再現する。
///
/// <para>各 index は <see cref="VectorIndexCatalog"/> に登録され、専用の payload テナント
/// (<see cref="VectorPayloadStore"/>) を持つ。書き込みは container の単一物理 PagedFile に乗るので、
/// アクティブ tx の <c>WalPageContext</c> 下で行えば自動的にその tx の WAL / ARIES に含まれ、
/// グラフ変更と原子整合する (tx 統合は GraphTransaction / 呼び出し側 autocommit が担う)。</para>
///
/// <para>6a 時点では検索は永続データの flat scan (HNSW 索引は 6d)。binding キーは Sequence で、
/// 世代照合 (slot 再利用 stale 棄却) は 6c で導入する。</para>
/// </summary>
internal sealed class PersistentVectorStore : IVectorStore
{
    private readonly SingleFileContainer _container;
    private readonly byte _catalogTenantId;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IndexHandle> _indexes = new(StringComparer.Ordinal);
    // catalog は遅延生成 (ctor が空テナントへヘッダページを書くのを避け、ベクトルを使わない DB の
    // on-disk レイアウトを撹乱しない)。CreateVectorIndex で初めて生成される。
    private VectorIndexCatalog? _catalog;

    public PersistentVectorStore(SingleFileContainer container, byte catalogTenantId)
    {
        _container = container;
        _catalogTenantId = catalogTenantId;
        // 既存 DB のみ、登録済み index を eager に開く。
        if (_container.HasTenant(catalogTenantId))
        {
            _catalog = new VectorIndexCatalog(_container.OpenTenant(catalogTenantId, PageKind.Header));
            foreach (var e in _catalog.Entries)
                _indexes[e.Spec.Name] = OpenHandle(e);
        }
    }

    private VectorIndexCatalog Catalog => _catalog ??= new VectorIndexCatalog(
        _container.OpenTenant(_catalogTenantId, PageKind.Header));

    private IndexHandle OpenHandle(VectorCatalogEntry e)
    {
        var payload = new VectorPayloadStore(
            _container.OpenTenant(e.PayloadTenant, PageKind.Header), e.Spec.Dimensions);
        return new IndexHandle(e.Spec, payload);
    }

    public void CreateVectorIndex(VectorIndexSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrEmpty(spec.Name))
            throw new VectorException("Vector index name must not be empty.");
        if (spec.Dimensions <= 0)
            throw new VectorException(
                $"Vector index '{spec.Name}' must have positive dimensions (was {spec.Dimensions}).");

        lock (_gate)
        {
            if (_indexes.ContainsKey(spec.Name))
                throw new VectorException($"Vector index '{spec.Name}' already exists.");
            var entry = Catalog.Register(spec);
            _indexes[spec.Name] = OpenHandle(entry);
        }
    }

    public void DropVectorIndex(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_indexes.Remove(name))
                throw new VectorException($"Vector index '{name}' does not exist.");
            Catalog.Unregister(name);
        }
    }

    public bool TryGetIndex(string name, out VectorIndexSpec spec)
    {
        spec = default!;
        if (string.IsNullOrEmpty(name)) return false;
        lock (_gate)
        {
            if (_indexes.TryGetValue(name, out var h)) { spec = h.Spec; return true; }
            return false;
        }
    }

    public void SetVector(EntityKind kind, long entityId, string indexName, ReadOnlySpan<float> vector)
    {
        IndexHandle h = GetIndex(indexName);
        if (kind != h.Spec.EntityKind)
            throw new VectorException(
                $"Vector index '{indexName}' is bound to {h.Spec.EntityKind} but got {kind}.");
        if (vector.Length != h.Spec.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {h.Spec.Dimensions} dimensions, got {vector.Length}.");
        // ARCH-5b: binding キーは slot Sequence へ正規化 (node.Value (gen 付き packed) を渡されうる)。
        // 世代照合による stale binding 棄却は 6c で導入する (現状 gen=0)。
        lock (_gate)
            h.Payload.Set(EntityRef.Sequence(entityId), 0, vector);
    }

    public void RemoveVector(EntityKind kind, long entityId, string indexName)
    {
        IndexHandle h = GetIndex(indexName);
        lock (_gate)
            h.Payload.Remove(EntityRef.Sequence(entityId));
    }

    public VectorSearchCursor KnnSearch(string indexName, ReadOnlySpan<float> query, int k)
    {
        if (k <= 0) throw new VectorException($"KnnSearch requires positive k (was {k}).");
        IndexHandle h = GetIndex(indexName);
        if (query.Length != h.Spec.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {h.Spec.Dimensions} dimensions, got {query.Length}.");

        int dim = h.Spec.Dimensions;
        var metric = h.Spec.Metric;
        var kind = h.Spec.EntityKind;
        var heap = new VectorKnnHeap(k);
        var buf = ArrayPool<float>.Shared.Rent(dim);
        try
        {
            var dest = buf.AsSpan(0, dim);
            lock (_gate)
            {
                long hwm = h.Payload.Hwm;
                for (long seq = 0; seq < hwm; seq++)
                    if (h.Payload.TryGet(seq, dest, out _))
                        heap.Offer(new VectorSearchResult(kind, seq, VectorMetrics.Score(metric, query, dest)));
            }
        }
        finally { ArrayPool<float>.Shared.Return(buf); }
        return new SortedVectorCursor(heap.ToSortedArray());
    }

    /// <summary>
    /// VEC-8 互換: 候補集合に対する KNN。候補が全件の 1/4 以下なら直接 gather、それ以上は
    /// 全件 scan + post-filter。<see cref="InMemoryVectorStore.KnnSearchFiltered"/> と同一の順序を返す。
    /// </summary>
    public VectorSearchCursor KnnSearchFiltered(
        string indexName, ReadOnlySpan<float> query, int k, EntityCandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (k <= 0) throw new VectorException($"KnnSearchFiltered requires positive k (was {k}).");
        IndexHandle h = GetIndex(indexName);
        if (query.Length != h.Spec.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {h.Spec.Dimensions} dimensions, got {query.Length}.");
        if (candidates.Count == 0 || candidates.Kind != h.Spec.EntityKind)
            return new SortedVectorCursor(Array.Empty<VectorSearchResult>());

        int dim = h.Spec.Dimensions;
        var metric = h.Spec.Metric;
        var kind = h.Spec.EntityKind;
        var heap = new VectorKnnHeap(k);
        var buf = ArrayPool<float>.Shared.Rent(dim);
        try
        {
            var dest = buf.AsSpan(0, dim);
            lock (_gate)
            {
                long hwm = h.Payload.Hwm;
                if (candidates.Count * 4 <= hwm || hwm == 0)
                {
                    foreach (var seq in candidates.Ids)
                        if (h.Payload.TryGet(seq, dest, out _))
                            heap.Offer(new VectorSearchResult(kind, seq, VectorMetrics.Score(metric, query, dest)));
                }
                else
                {
                    for (long seq = 0; seq < hwm; seq++)
                    {
                        if (!candidates.Contains(kind, seq)) continue;
                        if (h.Payload.TryGet(seq, dest, out _))
                            heap.Offer(new VectorSearchResult(kind, seq, VectorMetrics.Score(metric, query, dest)));
                    }
                }
            }
        }
        finally { ArrayPool<float>.Shared.Return(buf); }
        return new SortedVectorCursor(heap.ToSortedArray());
    }

    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName, IReadOnlyList<ReadOnlyMemory<float>> queries, int k)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (k <= 0) throw new VectorException($"KnnSearchBatch requires positive k (was {k}).");
        if (queries.Count == 0) return Array.Empty<VectorSearchCursor>();

        IndexHandle h = GetIndex(indexName);
        for (int q = 0; q < queries.Count; q++)
            if (queries[q].Length != h.Spec.Dimensions)
                throw new VectorException(
                    $"Vector index '{indexName}' expects {h.Spec.Dimensions} dimensions, " +
                    $"got {queries[q].Length} at query[{q}].");

        int dim = h.Spec.Dimensions;
        var metric = h.Spec.Metric;
        var kind = h.Spec.EntityKind;
        int Q = queries.Count;
        var heaps = new VectorKnnHeap[Q];
        for (int q = 0; q < Q; q++) heaps[q] = new VectorKnnHeap(k);

        var buf = ArrayPool<float>.Shared.Rent(dim);
        try
        {
            var dest = buf.AsSpan(0, dim);
            lock (_gate)
            {
                long hwm = h.Payload.Hwm;
                for (long seq = 0; seq < hwm; seq++)
                {
                    if (!h.Payload.TryGet(seq, dest, out _)) continue;
                    for (int q = 0; q < Q; q++)
                        heaps[q].Offer(new VectorSearchResult(kind, seq, VectorMetrics.Score(metric, queries[q].Span, dest)));
                }
            }
        }
        finally { ArrayPool<float>.Shared.Return(buf); }

        var cursors = new VectorSearchCursor[Q];
        for (int q = 0; q < Q; q++) cursors[q] = new SortedVectorCursor(heaps[q].ToSortedArray());
        return cursors;
    }

    /// <summary>
    /// abort の before-image undo 後 (<c>ReloadStoreMeta</c> 経由) と clean reopen で呼ばれ、
    /// 復元された catalog / payload ページから in-memory 状態を再同期する。
    /// </summary>
    public void ReloadAll()
    {
        lock (_gate)
        {
            _catalog?.Reload();
            foreach (var h in _indexes.Values)
                h.Payload.ReloadMeta();
        }
    }

    private IndexHandle GetIndex(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_indexes.TryGetValue(name, out var h))
                throw new VectorException($"Vector index '{name}' does not exist.");
            return h;
        }
    }

    private sealed record IndexHandle(VectorIndexSpec Spec, VectorPayloadStore Payload);
}
