using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// 各行のVertexラベルを読み取り、
/// 呼び出し側が渡す lookup (通常 <c>ISchemaEditor.GetLabelName</c>) でラベル名を解決して
/// UTF-8 文字列列を末尾に付加する。
/// </summary>
/// <remarks>
/// <see cref="PropertyLookupOperator"/> と同じ形状なので、後段のフィルタ / プロジェクションと
/// 同様に合成できる。LabelId が未知の場合は空文字列を放出する (ストリーム全体を失敗させない)。
/// </remarks>
internal sealed class LabelNameLookupOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _vertexColumn;
    private readonly Func<LabelId, string?> _labelNameLookup;
    private ITransaction? _tx;
    private TupleSlot[]? _buffer;
    private TupleSchema? _schema;
    private byte[]? _currentBytes;

    public LabelNameLookupOperator(
        IPhysicalOperator source,
        int vertexColumn,
        Func<LabelId, string?> labelNameLookup)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _vertexColumn = vertexColumn;
        _labelNameLookup = labelNameLookup ?? throw new ArgumentNullException(nameof(labelNameLookup));
    }

    public TupleSchema Schema => _schema ?? new TupleSchema([]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer!);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        var cols = new List<ColumnDefinition>(_source.Schema.Columns)
        {
            new ColumnDefinition("label", TupleSlotType.Utf8String)
        };
        _schema = new TupleSchema(cols);
        _buffer = new TupleSlot[cols.Count];
    }

    public bool MoveNext()
    {
        if (!_source.MoveNext()) return false;
        var cur = _source.Current;
        int srcCols = _source.Schema.Columns.Count;
        for (int i = 0; i < srcCols; i++) _buffer![i] = cur[i];

        var vertexId = new VertexId(cur[_vertexColumn].LongValue);
        var labelId = _tx!.Vertices.Read(vertexId).Label;
        var name = _labelNameLookup(labelId) ?? string.Empty;
        _currentBytes = System.Text.Encoding.UTF8.GetBytes(name);
        _buffer![srcCols] = new TupleSlot
        {
            Type = TupleSlotType.Utf8String,
            BytesOffset = 0,
            BytesLength = _currentBytes.Length,
        };

        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public ReadOnlySpan<byte> GetBytes(int column)
    {
        int labelCol = _source.Schema.Columns.Count;
        if (column == labelCol) return _currentBytes ?? ReadOnlySpan<byte>.Empty;
        return _source.GetBytes(column);
    }

    public void Dispose() => _source.Dispose();
}
