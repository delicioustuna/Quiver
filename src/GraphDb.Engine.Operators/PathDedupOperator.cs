using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

/// <summary>
/// Deduplicates rows from the source operator based on specified key columns.
/// Key columns must contain NodeId, RelationshipId, or Int64 values (uses LongValue for hashing).
/// First occurrence of each unique key is emitted; subsequent duplicates are dropped.
/// </summary>
public sealed class PathDedupOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int[] _keyColumns;
    private ITransaction? _tx;
    private HashSet<string>? _seen;

    public PathDedupOperator(IPhysicalOperator source, params int[] keyColumns)
    {
        if (keyColumns.Length == 0) throw new ArgumentException("At least one key column required.", nameof(keyColumns));
        _source = source;
        _keyColumns = keyColumns;
    }

    public TupleSchema Schema => _source.Schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => _source.Current;

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _seen = new HashSet<string>(StringComparer.Ordinal);
    }

    public bool MoveNext()
    {
        while (_source.MoveNext())
        {
            string key = ComputeKey(_source.Current);
            if (_seen!.Add(key))
            {
                var s = Statistics;
                s.RowsProduced++;
                Statistics = s;
                return true;
            }
        }
        return false;
    }

    private string ComputeKey(TupleRef tuple)
    {
        if (_keyColumns.Length == 1)
            return tuple[_keyColumns[0]].LongValue.ToString();

        // For multi-column keys, concatenate values with a separator unlikely to appear in data.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _keyColumns.Length; i++)
        {
            if (i > 0) sb.Append('\x1F');
            sb.Append(tuple[_keyColumns[i]].LongValue);
        }
        return sb.ToString();
    }

    public ReadOnlySpan<byte> GetBytes(int column) => _source.GetBytes(column);
    public void Dispose() => _source.Dispose();
}
