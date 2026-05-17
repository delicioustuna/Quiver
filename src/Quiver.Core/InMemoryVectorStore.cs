using System.Threading;

namespace Quiver.Core;

/// <summary>
/// Reference flat-scan <see cref="IVectorStore"/>. Holds every vector in a
/// per-index dictionary keyed by <c>(EntityKind, EntityId)</c> and scores every
/// stored vector against the query on each <see cref="KnnSearch"/>. ANN
/// structures land in follow-up tasks; this implementation exists to (a) pin
/// down the contract behaviour and (b) back unit tests / smoke samples.
/// VEC-1.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Index> _indexes = new(StringComparer.Ordinal);

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

    public void DropVectorIndex(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_indexes.Remove(name))
                throw new VectorException($"Vector index '{name}' does not exist.");
        }
    }

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
            idx.Vectors[new VectorKey(kind, entityId)] = copy;
        }
    }

    public void RemoveVector(EntityKind kind, long entityId, string indexName)
    {
        var idx = GetIndex(indexName);
        lock (_gate)
        {
            idx.Vectors.Remove(new VectorKey(kind, entityId));
        }
    }

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
    private static float Score(DistanceMetric metric, float[] q, float[] v)
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
            Array.Sort(arr, static (a, b) => b.Score.CompareTo(a.Score));
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
