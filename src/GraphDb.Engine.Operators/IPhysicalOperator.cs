using System.Runtime.InteropServices;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

/// <summary>物理演算子の基底契約。Volcano イテレータモデル。</summary>
public interface IPhysicalOperator : IDisposable
{
    void Open(ITransaction tx);
    bool MoveNext();
    TupleRef Current { get; }
    TupleSchema Schema { get; }
    OperatorStatistics Statistics { get; }
    ReadOnlySpan<byte> GetBytes(int column) => ReadOnlySpan<byte>.Empty;
}

/// <summary>演算子間で受け渡されるタプル参照。背後はオペレータ内のバッファ。</summary>
public readonly ref struct TupleRef
{
    private readonly Span<TupleSlot> _slots;

    public TupleRef(Span<TupleSlot> slots) => _slots = slots;

    public int ColumnCount => _slots.Length;
    public ref TupleSlot this[int i] => ref _slots[i];
}

/// <summary>タプルの 1 列分。型ごとのユニオン。</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct TupleSlot
{
    [FieldOffset(0)] public TupleSlotType Type;
    [FieldOffset(8)] public long LongValue;
    [FieldOffset(8)] public double DoubleValue;
    [FieldOffset(16)] public int BytesOffset;
    [FieldOffset(20)] public int BytesLength;
}

public enum TupleSlotType : byte
{
    Null = 0,
    NodeId = 1,
    RelationshipId = 2,
    Bool = 3,
    Int64 = 4,
    Double = 5,
    Utf8String = 6,
    Bytes = 7,
}

public sealed class TupleSchema
{
    public TupleSchema(IReadOnlyList<ColumnDefinition> columns) => Columns = columns;
    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public int IndexOf(string name)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (Columns[i].Name == name) return i;
        return -1;
    }
}

public sealed record ColumnDefinition(string Name, TupleSlotType Type);

public struct OperatorStatistics
{
    public long RowsProduced;
    public long ExecutionTicks;
    public long PageReadsLogical;
    public long PageReadsPhysical;
}
