using System.Runtime.InteropServices;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>物理演算子の基底契約。Volcano イテレータモデル。</summary>
internal interface IPhysicalOperator : IDisposable
{
    void Open(ITransaction tx);
    bool MoveNext();
    TupleRef Current { get; }
    TupleSchema Schema { get; }
    OperatorStatistics Statistics { get; }
    ReadOnlySpan<byte> GetBytes(int column) => ReadOnlySpan<byte>.Empty;
}

/// <summary>演算子間で受け渡されるタプル参照。背後はオペレータ内のバッファ。</summary>
internal readonly ref struct TupleRef
{
    private readonly Span<TupleSlot> _slots;

    public TupleRef(Span<TupleSlot> slots) => _slots = slots;

    public int ColumnCount => _slots.Length;
    public ref TupleSlot this[int i] => ref _slots[i];
}

/// <summary>タプルの 1 列分。型ごとのユニオン。</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct TupleSlot
{
    [FieldOffset(0)] public TupleSlotType Type;
    [FieldOffset(8)] public long LongValue;
    [FieldOffset(8)] public double DoubleValue;
    [FieldOffset(16)] public int BytesOffset;
    [FieldOffset(20)] public int BytesLength;
}

internal enum TupleSlotType : byte
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

internal sealed class TupleSchema
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

internal sealed record ColumnDefinition(string Name, TupleSlotType Type);

internal struct OperatorStatistics
{
    public long RowsProduced;
    public long ExecutionTicks;
    public long PageReadsLogical;
    public long PageReadsPhysical;

    /// <summary>
    /// Number of expansions that took the adjacency-block fast path.
    /// Compare against <see cref="RelationshipScanRecords"/> and the global
    /// <c>IGraphAccessMethods.AdjacencyFallbackCount</c> to attribute plan choice.
    /// </summary>
    public long AdjacencyBlockHits;

    /// <summary>
    /// Number of relationship records inspected by
    /// <see cref="RelationshipScanExpandOperator"/> (live records only — deleted
    /// rows are skipped by <c>IRelationshipStore.Scan</c>).
    /// </summary>
    public long RelationshipScanRecords;

    /// <summary>
    /// Number of <see cref="IPredicate.Evaluate"/> calls made by
    /// <see cref="BitmapFilterOperator"/>. With predicates ordered most-selective
    /// first this is lower than <c>predicates.Count * rowsIn</c> because each
    /// later predicate only sees rows still surviving in the page bitmap.
    /// </summary>
    public long PredicateEvaluations;
}
