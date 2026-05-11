using Quiver.Core;
using Quiver.Operators;

namespace Quiver;

public sealed class QueryResult : IDisposable
{
    private readonly List<QueryRow> _rows;

    public TupleSchema Schema { get; }
    public OperatorStatistics Statistics { get; }

    internal QueryResult(TupleSchema schema, OperatorStatistics statistics, List<QueryRow> rows)
    {
        Schema = schema;
        Statistics = statistics;
        _rows = rows;
    }

    public IEnumerable<QueryRow> Rows() => _rows;
    public void Dispose() { }
}

public readonly struct QueryRow
{
    private readonly TupleSlot[] _slots;
    private readonly byte[]?[]? _byteData;

    internal QueryRow(TupleSlot[] slots, byte[]?[]? byteData = null)
    {
        _slots = slots;
        _byteData = byteData;
    }

    public int ColumnCount => _slots.Length;
    public TupleSlotType GetSlotType(int column) => _slots[column].Type;
    public long GetInt64(int column) => _slots[column].LongValue;
    public NodeId GetNodeId(int column) => new(_slots[column].LongValue);
    public RelationshipId GetRelationshipId(int column) => new(_slots[column].LongValue);
    public double GetDouble(int column) => _slots[column].DoubleValue;
    public bool GetBoolean(int column) => _slots[column].LongValue != 0;

    public string GetString(int column)
        => _byteData?[column] is { } b ? System.Text.Encoding.UTF8.GetString(b) : string.Empty;

    public byte[] GetBytes(int column)
        => _byteData?[column] ?? Array.Empty<byte>();
}
