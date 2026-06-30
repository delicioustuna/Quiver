using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 2 つ以上の子演算子 (通常 <see cref="FullTextScanOperator"/> BM25 +
/// <see cref="KnnNodeSourceOperator"/>) のランク済み NodeId ストリームを
/// Reciprocal Rank Fusion で融合し、top-<c>k</c> を融合順で放出する演算子。
/// </summary>
/// <remarks>
/// RRF スコアは <c>Σ_i 1/(k0 + rank_i(d))</c>、k0=60、rank は各子ストリーム内で 1-based。
/// ランクのみに依存するため KNN / BM25 リーフの「スコア非公開」方針をそのまま維持する。
/// 各子は <see cref="Open"/> で完全に drain される (k は数十件なのでメモリは問題にならない)。
/// 融合スコアの同点は entity id 昇順で決定論的に解決する。可視性は子が既に保証済み。
/// <para>
/// 前提条件: 全子が同一の packed <see cref="NodeId"/> 空間で ID を放出すること。
/// 現行の子 (text-first BM25 + vector-first KNN) はこれを満たす。
/// </para>
/// </remarks>
internal sealed class FusionOperator : IPhysicalOperator
{
    private const double K0 = 60.0;

    private readonly IPhysicalOperator[] _children;
    private readonly int[] _childColumns;
    private readonly int _k;
    private long[] _results = Array.Empty<long>();
    private int _pos = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FusionOperator(IPhysicalOperator[] children, int[] childColumns, int k)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(childColumns);
        if (children.Length == 0)
            throw new ArgumentException("Fusion requires at least one child operator.", nameof(children));
        if (children.Length != childColumns.Length)
            throw new ArgumentException("children / childColumns length mismatch.", nameof(childColumns));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        _children = children;
        _childColumns = childColumns;
        _k = k;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        // RRF 累積: 各子のランク済みストリームを走査し、entity の累積スコアに
        // 1/(k0 + rank) (rank 1-based) を加算する。複数の子で上位に入る文書がブーストされる。
        var scores = new Dictionary<long, double>();
        for (int c = 0; c < _children.Length; c++)
        {
            var child = _children[c];
            int col = _childColumns[c];
            child.Open(tx);
            int rank = 0;
            while (child.MoveNext())
            {
                var slot = child.Current[col];
                if (slot.Type != TupleSlotType.NodeId) continue;
                rank++;
                long id = slot.LongValue;
                double contrib = 1.0 / (K0 + rank);
                scores[id] = scores.TryGetValue(id, out var prev) ? prev + contrib : contrib;
            }
        }

        var ranked = new List<KeyValuePair<long, double>>(scores);
        ranked.Sort(static (a, b) =>
        {
            int cmp = b.Value.CompareTo(a.Value);
            return cmp != 0 ? cmp : a.Key.CompareTo(b.Key);
        });
        _results = ranked.Take(_k).Select(kv => kv.Key).ToArray();
        _pos = -1;
    }

    public bool MoveNext()
    {
        if (_pos + 1 >= _results.Length) return false;
        _pos++;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _results[_pos] };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose()
    {
        foreach (var child in _children)
            child.Dispose();
    }
}
