namespace Yatagarasu.Core;

/// <summary>
/// KNN スコアリングの共有ヘルパ。primary scan と immutable segment が同一の
/// bounded top-k / 決定的順序 / metric 解釈を共有するために切り出した。
/// </summary>
internal static class VectorMetrics
{
    /// <summary>
    /// HIGHER = より類似になる値を返す (単一 max-heap で全 metric を扱える)。Euclidean は
    /// -distance を返す。実際の計算は <see cref="VectorScorer"/> (SIMD) へ委譲する。
    /// </summary>
    public static float Score(DistanceMetric metric, ReadOnlySpan<float> q, ReadOnlySpan<float> v)
        => metric switch
        {
            DistanceMetric.Cosine => VectorScorer.Cosine(q, v),
            DistanceMetric.Dot => VectorScorer.Dot(q, v),
            DistanceMetric.Euclidean => -VectorScorer.Euclidean(q, v),
            _ => throw new VectorException($"Unknown distance metric: {metric}."),
        };
}

/// <summary>
/// サイズ k の bounded max-heap。Score 上位 k 件を保持する (min-heap で root = 現 top-k の最悪)。
/// Offer は O(log k)、最終抽出は O(k log k)。<see cref="ToSortedArray"/> は score 降順 / 同点は
/// EntityId 昇順の決定的順序 (gather / scan / batch 経路が一致する)。
/// </summary>
internal sealed class VectorKnnHeap(int capacity, bool rejectNonFiniteScores = false)
{
    private readonly VectorSearchResult[] _items = new VectorSearchResult[capacity];
    private int _count;

    public void Offer(VectorSearchResult r)
    {
        if (!float.IsFinite(r.Score))
        {
            if (rejectNonFiniteScores)
                throw new VectorException("Vector search produced a non-finite score.");
            return;
        }
        if (_items.Length == 0) return;
        if (_count < _items.Length)
        {
            _items[_count++] = r;
            SiftUpMin(_count - 1);
            return;
        }
        if (Compare(r, _items[0]) < 0)
        {
            _items[0] = r;
            SiftDownMin(0);
        }
    }

    public VectorSearchResult[] ToSortedArray()
    {
        var arr = new VectorSearchResult[_count];
        Array.Copy(_items, arr, _count);
        Array.Sort(arr, Compare);
        return arr;
    }

    private static int Compare(VectorSearchResult a, VectorSearchResult b)
    {
        int score = b.Score.CompareTo(a.Score);
        return score != 0 ? score : a.EntityId.CompareTo(b.EntityId);
    }

    private void SiftUpMin(int i)
    {
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (Compare(_items[p], _items[i]) >= 0) break;
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
            if (l < n && Compare(_items[l], _items[m]) > 0) m = l;
            if (r < n && Compare(_items[r], _items[m]) > 0) m = r;
            if (m == i) break;
            (_items[m], _items[i]) = (_items[i], _items[m]);
            i = m;
        }
    }
}

/// <summary>事前ソート済み <see cref="VectorSearchResult"/> 配列を 1 件ずつ返すカーソル。</summary>
internal sealed class SortedVectorCursor(VectorSearchResult[] sorted) : VectorSearchCursor
{
    private int _i = -1;
    public override bool MoveNext() => ++_i < sorted.Length;
    public override VectorSearchResult Current => sorted[_i];
}
