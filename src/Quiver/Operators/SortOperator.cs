using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// GC-3: blocking sort. Drains the entire input on <see cref="Open"/>, captures
/// each row's slots and any UTF-8 / bytes payloads, then sorts by a single
/// column. Subsequent <see cref="MoveNext"/> calls stream the materialized
/// rows in sorted order.
///
/// Memory: O(rows) — every input row is held in managed memory. Callers that
/// only need top-N should stack <see cref="LimitOperator"/> above this; we
/// don't fuse limit + sort yet because the gain is small relative to the
/// terminal materialization the rest of the engine already does.
/// </summary>
public sealed class SortOperator : IPhysicalOperator
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

        // Null sorts to the end regardless of direction reversal (callers can
        // flip via descending). Treating Null specially keeps the comparison
        // total: missing properties become a single coherent bucket.
        bool aNull = sa.Type == TupleSlotType.Null;
        bool bNull = sb.Type == TupleSlotType.Null;
        if (aNull && bNull) return 0;
        if (aNull) return 1;
        if (bNull) return -1;

        return sa.Type switch
        {
            TupleSlotType.Double => sa.DoubleValue.CompareTo(sb.DoubleValue),
            TupleSlotType.Utf8String or TupleSlotType.Bytes => CompareBytes(a.Bytes?[col], b.Bytes?[col]),
            // NodeId / RelationshipId / Int64 / Bool all use LongValue.
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
