using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// ソースオペレータからの行を、指定したキー列に基づいて重複排除する。
/// キー列には VertexId / EdgeId / Int64 のいずれかを保持する必要がある (ハッシュは LongValue を使用)。
/// 各ユニークキーの最初の出現を放出し、以降の重複行はドロップする。
/// </summary>
internal sealed class PathDedupOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int[] _keyColumns;
    private ITransaction? _tx;
    private HashSet<string>? _seen;

    public PathDedupOperator(IPhysicalOperator source, params int[] keyColumns)
    {
        if (keyColumns.Length == 0) throw new ArgumentException("少なくとも 1 つのキー列が必要です。", nameof(keyColumns));
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

        // 複数列キーの場合、データに出現しにくい区切り文字で値を連結する。
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
