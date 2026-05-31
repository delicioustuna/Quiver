using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Operators;

/// <summary>
/// GC-1: implements Gremlin's <c>.label()</c> step. Reads the node label per
/// row and appends a UTF-8 string column carrying the label name resolved
/// via a caller-supplied lookup (typically <c>ISchemaApi.GetLabelName</c>).
/// </summary>
/// <remarks>
/// Mirrors <see cref="PropertyLookupOperator"/>'s shape so it composes the
/// same way with downstream filters / projections. The lookup callback
/// returns null when the LabelId is unknown — the operator emits an empty
/// string in that case rather than failing the whole stream.
/// </remarks>
public sealed class LabelNameLookupOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _nodeColumn;
    private readonly Func<LabelId, string?> _labelNameLookup;
    private ITransaction? _tx;
    private TupleSlot[]? _buffer;
    private TupleSchema? _schema;
    private byte[]? _currentBytes;

    public LabelNameLookupOperator(
        IPhysicalOperator source,
        int nodeColumn,
        Func<LabelId, string?> labelNameLookup)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _nodeColumn = nodeColumn;
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

        var nodeId = new NodeId(cur[_nodeColumn].LongValue);
        var labelId = _tx!.Nodes.Read(nodeId).Label;
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
