using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// ブロッキングソート。<see cref="Open"/> で入力を全行排出し、各行のスロットと UTF-8 / bytes
/// ペイロードを保持してから単一列でソートする。以降の <see cref="MoveNext"/> はソート済み行を
/// ストリーミングする。
/// </summary>
/// <remarks>
/// メモリ: O(rows) — 全入力行をマネージドメモリに保持する。top-N だけ必要な場合は
/// 上段に <see cref="LimitOperator"/> を積む。limit + sort の融合は未実装だが、
/// エンジンの終端物質化に対して効果が小さいため後回し。
/// </remarks>
internal sealed class SortOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sortColumn;
    private readonly bool _descending;
    private List<Row>? _rows;
    private int _index = -1;
    private Row _current;

    public SortOperator(IPhysicalOperator source, int sortColumn, bool descending = false)
    {
        _source = source;
        _sortColumn = sortColumn;
        _descending = descending;
    }

    public TupleSchema Schema => _source.Schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_current.Slots);

    public void Open(ITransaction tx)
    {
        _source.Open(tx);
        _rows = new List<Row>();
        while (_source.MoveNext())
        {
            var src = _source.Current;
            int n = src.ColumnCount;
            var slots = new TupleSlot[n];
            byte[]?[]? bytes = null;
            for (int i = 0; i < n; i++)
            {
                slots[i] = src[i];
                if (slots[i].Type is TupleSlotType.Utf8String or TupleSlotType.Bytes)
                {
                    bytes ??= new byte[]?[n];
                    bytes[i] = _source.GetBytes(i).ToArray();
                }
            }
            _rows.Add(new Row(slots, bytes));
        }

        var sortCol = _sortColumn;
        var desc = _descending;
        _rows.Sort((a, b) =>
        {
            int cmp = Compare(a, b, sortCol);
            return desc ? -cmp : cmp;
        });
    }

    public bool MoveNext()
    {
        _index++;
        if (_index >= _rows!.Count) return false;
        _current = _rows[_index];
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public ReadOnlySpan<byte> GetBytes(int column)
    {
        var bs = _current.Bytes;
        if (bs is null || column >= bs.Length) return ReadOnlySpan<byte>.Empty;
        return bs[column] ?? ReadOnlySpan<byte>.Empty;
    }

    public void Dispose()
    {
        _source.Dispose();
        _rows = null;
    }

    private static int Compare(in Row a, in Row b, int col)
    {
        var sa = a.Slots[col];
        var sb = b.Slots[col];

        // Null は方向反転に関わらず末尾にソートする。Null を特別扱いすることで
        // 比較が全順序になり、欠損プロパティが一つの一貫したバケットに収まる。
        bool aNull = sa.Type == TupleSlotType.Null;
        bool bNull = sb.Type == TupleSlotType.Null;
        if (aNull && bNull) return 0;
        if (aNull) return 1;
        if (bNull) return -1;

        return sa.Type switch
        {
            TupleSlotType.Double => sa.DoubleValue.CompareTo(sb.DoubleValue),
            TupleSlotType.Utf8String or TupleSlotType.Bytes => CompareBytes(a.Bytes?[col], b.Bytes?[col]),
            // VertexId / EdgeId / Int64 / Bool はすべて LongValue を使う。
            _ => sa.LongValue.CompareTo(sb.LongValue),
        };
    }

    private static int CompareBytes(byte[]? a, byte[]? b)
    {
        var aa = a ?? Array.Empty<byte>();
        var bb = b ?? Array.Empty<byte>();
        return aa.AsSpan().SequenceCompareTo(bb.AsSpan());
    }

    private readonly struct Row
    {
        public readonly TupleSlot[] Slots;
        public readonly byte[]?[]? Bytes;
        public Row(TupleSlot[] slots, byte[]?[]? bytes) { Slots = slots; Bytes = bytes; }
    }
}
