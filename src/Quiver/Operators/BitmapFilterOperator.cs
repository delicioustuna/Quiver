using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// PW-12: Batch filter that evaluates multiple predicates against a fixed-size
/// row buffer using a <see cref="PageSelectionBitmap"/>. Predicates are evaluated
/// in caller-supplied order; later predicates only see rows still set in the
/// bitmap, so when the caller (or <c>QueryOptimizer</c>) orders by
/// most-selective-first the total <c>PredicateEvaluations</c> count is
/// strictly lower than the naive <c>predicates.Count * rowsIn</c>.
///
/// Row-oriented pages are fine — the bitmap doesn't need PAX. When a future
/// adjacency / property column view goes column-oriented the same scan shape
/// keeps working without operator changes.
/// </summary>
internal sealed class BitmapFilterOperator : IPhysicalOperator
{
    private const int DefaultBatchSize = 64;

    private readonly IPhysicalOperator _source;
    private readonly IPredicate[] _predicates;
    private readonly int _batchSize;
    private ITransaction? _tx;
    private TupleSlot[]? _batch;
    private ulong[]? _bitmapWords;
    private TupleSlot[]? _outputRow;
    private int _cols;
    private int _batchCount;
    private int _outputIdx;
    private bool _sourceExhausted;

    public BitmapFilterOperator(IPhysicalOperator source, IReadOnlyList<IPredicate> predicatesInSelectivityOrder)
        : this(source, predicatesInSelectivityOrder, DefaultBatchSize) { }

    internal BitmapFilterOperator(
        IPhysicalOperator source,
        IReadOnlyList<IPredicate> predicatesInSelectivityOrder,
        int batchSize)
    {
        if (predicatesInSelectivityOrder == null) throw new ArgumentNullException(nameof(predicatesInSelectivityOrder));
        if (predicatesInSelectivityOrder.Count == 0)
            throw new ArgumentException("at least one predicate required", nameof(predicatesInSelectivityOrder));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _source = source;
        _predicates = [.. predicatesInSelectivityOrder];
        _batchSize = batchSize;
    }

    public TupleSchema Schema => _source.Schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_outputRow!);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _cols = _source.Schema.Columns.Count;
        _batch = new TupleSlot[_batchSize * Math.Max(_cols, 1)];
        _bitmapWords = new ulong[(_batchSize + 63) >> 6];
        _outputRow = new TupleSlot[Math.Max(_cols, 1)];
        _batchCount = 0;
        _outputIdx = -1;
        _sourceExhausted = false;
    }

    public bool MoveNext()
    {
        while (true)
        {
            // 現在のバッチで生き残った行を排出する。
            if (_batchCount > 0)
            {
                var bm = new PageSelectionBitmap(_bitmapWords!, _batchCount);
                while (++_outputIdx < _batchCount)
                {
                    if (!bm.IsSet(_outputIdx)) continue;
                    int baseIdx = _outputIdx * _cols;
                    for (int c = 0; c < _cols; c++) _outputRow![c] = _batch![baseIdx + c];
                    var s = Statistics; s.RowsProduced++; Statistics = s;
                    return true;
                }
            }

            if (_sourceExhausted) return false;

            // Refill batch.
            _batchCount = 0;
            _outputIdx = -1;
            while (_batchCount < _batchSize)
            {
                if (!_source.MoveNext()) { _sourceExhausted = true; break; }
                var src = _source.Current;
                int baseIdx = _batchCount * _cols;
                for (int c = 0; c < _cols; c++) _batch![baseIdx + c] = src[c];
                _batchCount++;
            }
            if (_batchCount == 0) return false;

            // Init bitmap to all rows live, then knock out failures pass-by-pass.
            var bitmap = new PageSelectionBitmap(_bitmapWords!, _batchCount);
            bitmap.SetAll();

            for (int p = 0; p < _predicates.Length; p++)
            {
                if (bitmap.PopCount() == 0) break;
                var pred = _predicates[p];
                var en = bitmap.GetEnumerator();
                while (en.MoveNext())
                {
                    int row = en.Current;
                    int baseIdx = row * _cols;
                    var rowSpan = new Span<TupleSlot>(_batch!, baseIdx, _cols);
                    var rref = new TupleRef(rowSpan);
                    bool keep = pred.Evaluate(in rref, _tx!);
                    bitmap.AndEquals(row, keep);
                    var s = Statistics; s.PredicateEvaluations++; Statistics = s;
                }
            }
        }
    }

    public ReadOnlySpan<byte> GetBytes(int column) => ReadOnlySpan<byte>.Empty;

    public void Dispose()
    {
        _source.Dispose();
        for (int i = 0; i < _predicates.Length; i++)
        {
            if (_predicates[i] is IDisposable d) d.Dispose();
        }
    }
}
