using System.Buffers;
using System.Threading;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// container テナントへベクトル payload を永続化する <see cref="IVectorStore"/>。
/// <see cref="InMemoryVectorStore"/> を置き換え、再起動を跨いで KNN を再現する。
///
/// <para>各 index は <see cref="VectorIndexCatalog"/> に登録され、専用の payload テナント
/// (<see cref="VectorIndexPayloadStore"/>) を持つ。書き込みは container の単一物理 PagedFile に乗るので、
/// アクティブ tx の <c>WalWriteSet</c> 下で行えば自動的にその tx の page-WAL に含まれ、
/// グラフ変更と原子整合する (tx 統合は GraphTransaction / 呼び出し側 autocommit が担う)。</para>
///
/// <para>検索は index 設定に応じて flat scan または HNSW を使う。
/// binding キーは Sequence で、世代照合により slot 再利用後の stale binding を棄却する。</para>
/// </summary>
internal sealed class PersistentVectorStore : IVectorStore
{
    private readonly SingleFileContainer _container;
    private readonly byte _catalogTenantId;
    // (kind, sequence) → 現世代を引く resolver。slot 再利用で別エンティティに化けた
    // stale binding を KNN read 時に弾くために使う。null = 旧経路 / テスト (世代照合なし)。
    private readonly Func<EntityKind, long, int>? _currentGeneration;
    private readonly VectorPayloadCacheBudget _cacheBudget;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IndexHandle> _indexes = new(StringComparer.Ordinal);
    // catalog は遅延生成 (ctor が空テナントへヘッダページを書くのを避け、ベクトルを使わない DB の
    // on-disk レイアウトを撹乱しない)。CreateVectorIndex で初めて生成される。
    private VectorIndexCatalog? _catalog;

    public PersistentVectorStore(
        SingleFileContainer container, byte catalogTenantId,
        Func<EntityKind, long, int>? currentGeneration = null,
        long vectorCacheBudgetBytes = 64L * 1024 * 1024)
    {
        _container = container;
        _catalogTenantId = catalogTenantId;
        _currentGeneration = currentGeneration;
        _cacheBudget = new VectorPayloadCacheBudget(vectorCacheBudgetBytes);
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
        var payload = new VectorIndexPayloadStore(
            _container.OpenTenant(e.PayloadTenant, PageKind.Header),
            e.Spec.Dimensions,
            e.Spec.ElementType,
            _cacheBudget);
        if (e.Spec.IndexKind == VectorIndexKind.FlatOnly)
            return new IndexHandle(e.Spec, payload, null);
        var hnsw = new HnswIndex(
            _container.OpenTenant(e.HnswTenant, PageKind.Header), payload, e.Spec);
        return new IndexHandle(e.Spec, payload, hnsw);
    }

    public void CreateVectorIndex(VectorIndexSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        VectorIndexSpecValidator.Validate(spec);

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
            if (!_indexes.TryGetValue(name, out var handle))
                throw new VectorException($"Vector index '{name}' does not exist.");
            using (handle.Write())
            {
                _indexes.Remove(name);
                Catalog.Unregister(name);
            }
        }
    }

    public IReadOnlyList<VectorIndexSpec> ListVectorIndexes()
    {
        lock (_gate) { return _indexes.Values.Select(h => h.Spec).ToList(); }
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
        // binding キーは slot Sequence へ正規化 (vertex.Value (gen 付き packed) を渡されうる)。
        // 現世代を payload に焼き込み、slot 再利用で別エンティティに化けた stale binding を
        // KNN read 時に弾けるようにする。resolver 無し (テスト) は 0。
        long seq = EntityRef.UnpackSequence(entityId);
        ushort gen = ResolveGen(kind, seq);
        using (h.Write())
        {
            h.Payload.Set(seq, gen, vector);
            h.Hnsw?.Upsert(seq);
        }
    }

    public void RemoveVector(EntityKind kind, long entityId, string indexName)
    {
        IndexHandle h = GetIndex(indexName);
        long seq = EntityRef.UnpackSequence(entityId);
        using (h.Write())
        {
            h.Payload.Remove(seq);
            h.Hnsw?.Delete(seq);
        }
    }

    /// <inheritdoc/>
    public bool TryGetVector(EntityKind kind, long entityId, string indexName, Span<float> destination)
    {
        IndexHandle h = GetIndex(indexName);
        if (kind != h.Spec.EntityKind) return false;
        if (destination.Length < h.Spec.Dimensions) return false;

        long seq = EntityRef.UnpackSequence(entityId);
        using (h.Read())
        {
            if (!h.Payload.TryGet(seq, destination, out var gen))
                return false;
            return IsLive(kind, seq, gen);
        }
    }

    public VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
    {
        options = VectorSearchOptionsValidator.Normalize(options);
        if (k <= 0) throw new VectorException($"KnnSearch requires positive k (was {k}).");
        IndexHandle h = GetIndex(indexName);
        if (h.Spec.IndexKind == VectorIndexKind.FlatOnly)
            throw new VectorException(
                $"Vector index '{indexName}' is FlatOnly and does not support vector-first KnnSearch. " +
                "Use ApplyDyadic for brute-force scoring.");
        if (query.Length != h.Spec.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {h.Spec.Dimensions} dimensions, got {query.Length}.");

        var kind = h.Spec.EntityKind;
        VectorSearchResult[] sorted;
        using (h.Read())
            sorted = h.Hnsw!.Search(
                query, k, kind, (seq, gen) => IsLive(kind, seq, gen), options);
        return new SortedVectorCursor(sorted);
    }

    /// <summary>
    /// ベンチマークと recall 検証用の exact top-k。HNSW を一切経由せず、payload の
    /// <c>[0, Hwm)</c> を全走査して <see cref="VectorKnnHeap"/> で上位 k 件を求める。
    /// 公開 API には露出させず、近似検索の独立した分母としてのみ使う。
    /// </summary>
    internal VectorSearchCursor KnnSearchExact(string indexName, ReadOnlySpan<float> query, int k)
    {
        if (k <= 0) throw new VectorException($"KnnSearchExact requires positive k (was {k}).");
        IndexHandle h = GetIndex(indexName);
        if (query.Length != h.Spec.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {h.Spec.Dimensions} dimensions, got {query.Length}.");

        int dim = h.Spec.Dimensions;
        var kind = h.Spec.EntityKind;
        var heap = new VectorKnnHeap(k);
        var buffer = ArrayPool<float>.Shared.Rent(dim);
        try
        {
            using (h.Read())
            {
                var vector = buffer.AsSpan(0, dim);
                for (long seq = 0; seq < h.Payload.Hwm; seq++)
                {
                    if (h.Payload.TryGet(seq, vector, out var gen) && IsLive(kind, seq, gen))
                    {
                        heap.Offer(new VectorSearchResult(
                            kind, seq, VectorMetrics.Score(h.Spec.Metric, query, vector)));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }

        return new SortedVectorCursor(heap.ToSortedArray());
    }

    /// <summary>
    /// ③: 候補集合に対する KNN。小候補 (全件の 1/4 以下) は直接 gather + brute (HNSW より速く exact)、
    /// 大候補 (低選択率) は HNSW 探索 + post-filter (ef オーバーサンプルで k 件を確保)。
    /// </summary>
    public VectorSearchCursor KnnSearchFiltered(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        EntityCandidateSet candidates,
        VectorSearchOptions? options = null)
    {
        options = VectorSearchOptionsValidator.Normalize(options);
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
        using (h.Read())
        {
            long hwm = h.Payload.Hwm;
            if (h.Hnsw is null || candidates.Count * 4 <= hwm || hwm == 0)
            {
                var heap = new VectorKnnHeap(k);
                var buf = ArrayPool<float>.Shared.Rent(dim);
                try
                {
                    var dest = buf.AsSpan(0, dim);
                    foreach (var seq in candidates.Ids)
                        if (h.Payload.TryGet(seq, dest, out var gen) && IsLive(kind, seq, gen))
                            heap.Offer(new VectorSearchResult(kind, seq, VectorMetrics.Score(metric, query, dest)));
                }
                finally { ArrayPool<float>.Shared.Return(buf); }
                return new SortedVectorCursor(heap.ToSortedArray());
            }
            var sorted = h.Hnsw.Search(query, k, kind,
                (seq, gen) => IsLive(kind, seq, gen),
                options,
                seq => candidates.Contains(kind, seq));
            return new SortedVectorCursor(sorted);
        }
    }

    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k,
        VectorSearchOptions? options = null)
    {
        options = VectorSearchOptionsValidator.Normalize(options);
        ArgumentNullException.ThrowIfNull(queries);
        if (k <= 0) throw new VectorException($"KnnSearchBatch requires positive k (was {k}).");
        if (queries.Count == 0) return Array.Empty<VectorSearchCursor>();

        IndexHandle h = GetIndex(indexName);
        if (h.Spec.IndexKind == VectorIndexKind.FlatOnly)
            throw new VectorException(
                $"Vector index '{indexName}' is FlatOnly and does not support vector-first KnnSearchBatch. " +
                "Use ApplyDyadic for brute-force scoring.");
        for (int q = 0; q < queries.Count; q++)
            if (queries[q].Length != h.Spec.Dimensions)
                throw new VectorException(
                    $"Vector index '{indexName}' expects {h.Spec.Dimensions} dimensions, " +
                    $"got {queries[q].Length} at query[{q}].");

        var kind = h.Spec.EntityKind;
        int Q = queries.Count;
        var cursors = new VectorSearchCursor[Q];
        using (h.Read())
        {
            for (int q = 0; q < Q; q++)
            {
                var sorted = h.Hnsw!.Search(
                    queries[q].Span,
                    k,
                    kind,
                    (seq, gen) => IsLive(kind, seq, gen),
                    options);
                cursors[q] = new SortedVectorCursor(sorted);
            }
        }
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
            if (_catalog is null) return;
            _catalog.Reload();
            // catalog を正本に _indexes を再構築する。abort で消えた index (CreateVectorIndex を
            // 中止した tx 分) の handle を落とし、catalog に残る index の payload meta を読み直す。
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in _catalog.Entries)
            {
                live.Add(e.Spec.Name);
                if (!_indexes.ContainsKey(e.Spec.Name))
                    _indexes[e.Spec.Name] = OpenHandle(e);
            }
            foreach (var name in _indexes.Keys.Where(n => !live.Contains(n)).ToList())
                _indexes.Remove(name);
            foreach (var h in _indexes.Values)
            {
                using (h.Write())
                {
                    h.Payload.ReloadMeta();
                    h.Hnsw?.ReloadFromPages();
                }
            }
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

    /// <summary>(kind, seq) の現世代を ushort に丸めて返す。resolver 無し / 範囲外は 0。</summary>
    private ushort ResolveGen(EntityKind kind, long seq)
    {
        if (_currentGeneration is null) return 0;
        int g = _currentGeneration(kind, seq);
        return g <= 0 ? (ushort)0 : (ushort)Math.Min(g, EntityRef.MaxGeneration);
    }

    /// <summary>
    /// payload に焼かれた世代 <paramref name="storedGen"/> が現在の slot 世代と一致するか。
    /// 不一致なら slot が再利用され別エンティティに化けた stale binding なので KNN から除外する。
    /// resolver 無し (テスト / 旧経路) は常に live 扱い。
    /// </summary>
    private bool IsLive(EntityKind kind, long seq, ushort storedGen)
    {
        if (_currentGeneration is null) return true;
        int cur = _currentGeneration(kind, seq);
        return cur >= 0 && (ushort)Math.Min(cur, EntityRef.MaxGeneration) == storedGen;
    }

    private sealed class IndexHandle(
        VectorIndexSpec spec,
        VectorIndexPayloadStore payload,
        HnswIndex? hnsw)
    {
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

        public VectorIndexSpec Spec { get; } = spec;
        public VectorIndexPayloadStore Payload { get; } = payload;
        public HnswIndex? Hnsw { get; } = hnsw;

        public LockScope Read() => new(_lock, write: false);
        public LockScope Write() => new(_lock, write: true);
    }

    private readonly struct LockScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;
        private readonly bool _write;

        public LockScope(ReaderWriterLockSlim @lock, bool write)
        {
            _lock = @lock;
            _write = write;
            if (write) @lock.EnterWriteLock();
            else @lock.EnterReadLock();
        }

        public void Dispose()
        {
            if (_write) _lock.ExitWriteLock();
            else _lock.ExitReadLock();
        }
    }
}
