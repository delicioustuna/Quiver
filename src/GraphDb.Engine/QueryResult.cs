using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;

namespace GraphDb.Engine;

public sealed class QueryResult : IDisposable
{
    public TupleSchema Schema { get; }
    public OperatorStatistics Statistics { get; }

    internal QueryResult(TupleSchema schema, OperatorStatistics statistics)
    {
        Schema = schema;
        Statistics = statistics;
    }

    public IEnumerable<QueryRow> Rows() => throw new NotImplementedException();
    public void Dispose() { }
}

public readonly struct QueryRow
{
    private readonly TupleSlot[] _slots;

    internal QueryRow(TupleSlot[] slots) => _slots = slots;

    public int ColumnCount => _slots.Length;
    public TupleSlotType GetSlotType(int column) => _slots[column].Type;
    public long GetInt64(int column) => _slots[column].LongValue;
    public NodeId GetNodeId(int column) => new(_slots[column].LongValue);
    public RelationshipId GetRelationshipId(int column) => new(_slots[column].LongValue);
    public double GetDouble(int column) => _slots[column].DoubleValue;
    public bool GetBoolean(int column) => _slots[column].LongValue != 0;
    public string GetString(int column) => throw new NotImplementedException();
    public byte[] GetBytes(int column) => throw new NotImplementedException();
}
