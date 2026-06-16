using System.Threading;

namespace Quiver.Core;

/// <summary>
/// 全件フラットスキャンの参照 <see cref="IVectorStore"/>。全ベクトルをインデックス毎の辞書に
/// <c>(EntityKind, EntityId)</c> をキーに保持し、<see cref="KnnSearch"/> の度に全ベクトルを
/// クエリに対してスコアリングする。
///
/// <para>これは <b>非永続の参照実装</b> であり、binary backend の既定ではない。
/// binary backend はベクトルと HNSW ANN 索引を
/// <see cref="Quiver.Storage.Records.PersistentVectorStore"/> +
/// <see cref="Quiver.Storage.Records.HnswIndex"/> で in-file に永続化する。
/// この in-memory ストアは (a) 単体テスト / smoke サンプル / fixtures、
/// (b) 永続ストアが満たすべき契約挙動の
/// 基準、のために残している。</para>
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Index> _indexes = new(StringComparer.Ordinal);

    /// <summary>新しいベクトルインデックスを作成する。空名 / 非正の次元 / 名前重複は <see cref="VectorException"/>。</summary>
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
            _indexes[spec.Name] = new Index(spec);
        }
    }

    /// <summary>指定名のベクトルインデックスを削除する。存在しなければ <see cref="VectorException"/>。</summary>
    public void DropVectorIndex(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_indexes.Remove(name))
                throw new VectorException($"Vector index '{name}' does not exist.");
        }
    }

    /// <summary>登録済み index の spec を返す。未登録は <c>false</c>。</summary>
    public bool TryGetIndex(string name, out VectorIndexSpec spec)
    {
        spec = default!;
        if (string.IsNullOrEmpty(name)) return false;
        lock (_gate)
        {
            if (_indexes.TryGetValue(name, out var idx))
            {
                spec = idx.Spec;
                return true;
            }
            return false;
        }
    }

    /// <summary>指定エンティティのベクトルを設定 (上書き) する。次元 / 種別不一致は <see cref="VectorException"/>。</summary>
    public void SetVector(
        EntityKind kind,
        long entityId,
        string indexName,
        ReadOnlySpan<float> vector)
    {
        var idx = GetIndex(indexName);
        if (kind != idx.Spec.EntityKind)
            throw new VectorException(
                $"Vector index '{indexName}' is bound to {idx.Spec.EntityKind} but got {kind}.");
        if (vector.Length != idx.Spec.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {idx.Spec.Dimensions} dimensions, got {vector.Length}.");

        var copy = vector.ToArray();
        lock (_gate)
        {
            // ARCH-5b: binding キーは slot Sequence。利用者は node.Value (gen 付き packed) を
            // 渡しうるが、グラフ側 (label index / adjacency / candidate set) は Sequence 空間で
            // 動くため、ここで slot へ正規化して KNN を整合させる。世代照合による stale binding
            // 検出 (slot 再利用で旧ベクトルを弾く) は ARCH-6 で導入する。
            idx.Vectors[new VectorKey(kind, EntityRef.Sequence(entityId))] = copy;
        }
    }

    /// <summary>指定エンティティのベクトルをインデックスから除去する。</summary>
    public void RemoveVector(EntityKind kind, long entityId, string indexName)
    {
        var idx = GetIndex(indexName);
        lock (_gate)
        {
            idx.Vectors.Remove(new VectorKey(kind, EntityRef.Sequence(entityId))); // ARCH-5b: slot key
        }
    }

    /// <summary>クエリベクトルに対する上位 <paramref name="k"/> 件の近傍を全件スキャンで検索する。</summary>
    public VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k)
    {
        if (k <= 0)
            throw new VectorException($"KnnSearch requires positive k (was {k}).");

        var idx = GetIndex(indexName);
        if (query.Length != idx.Spec.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {idx.Spec.Dimensions} dimensions, got {query.Length}.");

        // Snapshot under the lock so the search runs against stable data.
        KeyValuePair<VectorKey, float[]>[] snapshot;
        DistanceMetric metric;
        lock (_gate)
        {
            snapshot = new KeyValuePair<VectorKey, float[]>[idx.Vectors.Count];
            int i = 0;
            foreach (var kv in idx.Vectors)
                snapshot[i++] = kv;
            metric = idx.Spec.Metric;
        }

        var queryCopy = query.ToArray();
        var heap = new BoundedMaxHeap(k);
        foreach (var kv in snapshot)
        {
            float score = Score(metric, queryCopy, kv.Value);
            heap.Offer(new VectorSearchResult(kv.Key.Kind, kv.Key.Id, score));
        }

        return new HeapCursor(heap.ToSortedArray());
    }

    /// <summary>
    /// gather-then-score 経路。<paramref name="candidates"/> が
    /// インデックス全件の 1/4 以下のとき、candidate ID を直接ルックアップして
    /// ヒット分だけ <see cref="VectorScorer"/> に流す。それ以上の比率では候補ヒット率が
    /// 高いとみなし全件スキャン + post-filter にフォールバックする。
    /// 結果は <see cref="IGraphAccessMethods.KnnSearchFiltered"/> 既定実装と完全に一致する
    /// (順序込み、スコアは相対誤差内)。
    /// </summary>
    public VectorSearchCursor KnnSearchFiltered(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        EntityCandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (k <= 0)
            throw new VectorException($"KnnSearchFiltered requires positive k (was {k}).");

        var idx = GetIndex(indexName);
        if (query.Length != idx.Spec.Dimensions)
            throw new VectorException(
                $"Vector index '{indexName}' expects {idx.Spec.Dimensions} dimensions, got {query.Length}.");

        // 候補集合が空 / kind 不一致 ⇒ 短絡。ベクトル辞書には触らない。
        if (candidates.Count == 0 || candidates.Kind != idx.Spec.EntityKind)
            return new HeapCursor(Array.Empty<VectorSearchResult>());

        // どちらの経路でも metric / snapshot を共有して race を避ける。
        List<KeyValuePair<VectorKey, float[]>>? gathered = null;
        KeyValuePair<VectorKey, float[]>[]? scanSnapshot = null;
        DistanceMetric metric;
        lock (_gate)
        {
            metric = idx.Spec.Metric;
            int total = idx.Vectors.Count;
            // gather パス: 候補が「全件の 1/4 以下」のとき直接ルックアップ。
            // 同点ボーダー (total == 0 を含む) も gather に倒す方が cheaper。
            if (candidates.Count * 4 <= total || total == 0)
            {
                gathered = new List<KeyValuePair<VectorKey, float[]>>(candidates.Count);
                foreach (var id in candidates.Ids)
                {
                    var key = new VectorKey(candidates.Kind, id);
                    if (idx.Vectors.TryGetValue(key, out var vec))
                        gathered.Add(new KeyValuePair<VectorKey, float[]>(key, vec));
                }
            }
            else
            {
                scanSnapshot = new KeyValuePair<VectorKey, float[]>[total];
                int i = 0;
                foreach (var kv in idx.Vectors) scanSnapshot[i++] = kv;
            }
        }

        var queryCopy = query.ToArray();
        var heap = new BoundedMaxHeap(k);

        if (gathered is not null)
        {
            foreach (var kv in gathered)
            {
                float score = Score(metric, queryCopy, kv.Value);
                heap.Offer(new VectorSearchResult(kv.Key.Kind, kv.Key.Id, score));
            }
        }
        else
        {
            foreach (var kv in scanSnapshot!)
            {
                if (!candidates.Contains(kv.Key.Kind, kv.Key.Id)) continue;
                float score = Score(metric, queryCopy, kv.Value);
                heap.Offer(new VectorSearchResult(kv.Key.Kind, kv.Key.Id, score));
            }
        }

        return new HeapCursor(heap.ToSortedArray());
    }

    /// <summary>
    /// 同一インデックスに対する複数クエリを単一 snapshot 上で評価する。
    /// outer = queries (Q), inner = corpus (N) でクエリベクトルを L1/L2 に滞留させ、
    /// ロックは snapshot 取得時の 1 回だけ。クエリごとに独立した <see cref="BoundedMaxHeap"/>
    /// を持つ。次元不一致はループ前に検出する。
    /// </summary>
    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (k <= 0)
            throw new VectorException($"KnnSearchBatch requires positive k (was {k}).");
        if (queries.Count == 0)
            return Array.Empty<VectorSearchCursor>();

        var idx = GetIndex(indexName);
        for (int q = 0; q < queries.Count; q++)
        {
            if (queries[q].Length != idx.Spec.Dimensions)
                throw new VectorException(
                    $"Vector index '{indexName}' expects {idx.Spec.Dimensions} dimensions, " +
                    $"got {queries[q].Length} at query[{q}].");
        }

        KeyValuePair<VectorKey, float[]>[] snapshot;
        DistanceMetric metric;
        lock (_gate)
        {
            snapshot = new KeyValuePair<VectorKey, float[]>[idx.Vectors.Count];
            int i = 0;
            foreach (var kv in idx.Vectors) snapshot[i++] = kv;
            metric = idx.Spec.Metric;
        }

        int Q = queries.Count;
        var heaps = new BoundedMaxHeap[Q];
        for (int q = 0; q < Q; q++) heaps[q] = new BoundedMaxHeap(k);

        for (int q = 0; q < Q; q++)
        {
            var qSpan = queries[q].Span;
            var heap = heaps[q];
            foreach (var kv in snapshot)
            {
                float score = Score(metric, qSpan, kv.Value);
                heap.Offer(new VectorSearchResult(kv.Key.Kind, kv.Key.Id, score));
            }
        }

        var cursors = new VectorSearchCursor[Q];
        for (int q = 0; q < Q; q++) cursors[q] = new HeapCursor(heaps[q].ToSortedArray());
        return cursors;
    }

    private Index GetIndex(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_indexes.TryGetValue(name, out var idx))
                throw new VectorException($"Vector index '{name}' does not exist.");
            return idx;
        }
    }

    // Score returns a value where HIGHER = more similar so a single max-heap
    // works across all metrics. Euclidean is therefore returned as -distance.
    // VEC-7: delegates to VectorScorer (SIMD via System.Numerics.Vector<float>).
    private static float Score(DistanceMetric metric, ReadOnlySpan<float> q, ReadOnlySpan<float> v)
    {
        return metric switch
        {
            DistanceMetric.Cosine => VectorScorer.Cosine(q, v),
            DistanceMetric.Dot => VectorScorer.Dot(q, v),
            DistanceMetric.Euclidean => -VectorScorer.Euclidean(q, v),
            _ => throw new VectorException($"Unknown distance metric: {metric}."),
        };
    }

    private readonly record struct VectorKey(EntityKind Kind, long Id);

    private sealed class Index(VectorIndexSpec spec)
    {
        public VectorIndexSpec Spec { get; } = spec;
        public Dictionary<VectorKey, float[]> Vectors { get; } = new();
    }

    // Bounded max-heap of size k that keeps the top-k by Score. Each Offer is
    // O(log k); final extraction is O(k log k).
    private sealed class BoundedMaxHeap(int capacity)
    {
        private readonly VectorSearchResult[] _items = new VectorSearchResult[capacity];
        private int _count;

        public void Offer(VectorSearchResult r)
        {
            if (_count < _items.Length)
            {
                _items[_count++] = r;
                SiftUpMin(_count - 1);
                return;
            }
            // Min-heap: root is the worst of the current top-k. If the new
            // score beats it, replace + sift down.
            if (r.Score > _items[0].Score)
            {
                _items[0] = r;
                SiftDownMin(0);
            }
        }

        public VectorSearchResult[] ToSortedArray()
        {
            var arr = new VectorSearchResult[_count];
            Array.Copy(_items, arr, _count);
            // VEC-8: deterministic order — score desc, then EntityId asc on ties
            // so gather / scan / batch paths all agree.
            Array.Sort(arr, static (a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                return c != 0 ? c : a.EntityId.CompareTo(b.EntityId);
            });
            return arr;
        }

        private void SiftUpMin(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_items[p].Score <= _items[i].Score) break;
                (_items[p], _items[i]) = (_items[i], _items[p]);
                i = p;
            }
        }

        private void SiftDownMin(int i)
        {
            int n = _count;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, m = i;
                if (l < n && _items[l].Score < _items[m].Score) m = l;
                if (r < n && _items[r].Score < _items[m].Score) m = r;
                if (m == i) break;
                (_items[m], _items[i]) = (_items[i], _items[m]);
                i = m;
            }
        }
    }

    private sealed class HeapCursor(VectorSearchResult[] sorted) : VectorSearchCursor
    {
        private int _i = -1;
        public override bool MoveNext() => ++_i < sorted.Length;
        public override VectorSearchResult Current => sorted[_i];
    }
}
