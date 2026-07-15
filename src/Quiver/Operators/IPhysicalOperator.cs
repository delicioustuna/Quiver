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
    HyperedgeId = 3,
    Bool = 4,
    Int64 = 5,
    Double = 6,
    Utf8String = 7,
    Bytes = 8,
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
    /// 隣接ブロック高速パスで展開された回数。
    /// <see cref="RelationshipScanRecords"/> やグローバルの
    /// <c>IGraphAccessMethods.AdjacencyFallbackCount</c> と比較してプラン選択を評価する。
    /// </summary>
    public long AdjacencyBlockHits;

    /// <summary>
    /// <see cref="RelationshipScanExpandOperator"/> が走査したリレーションシップレコード数
    /// (生存レコードのみ — 削除済み行は <c>IRelationshipStore.Scan</c> がスキップ)。
    /// </summary>
    public long RelationshipScanRecords;

    /// <summary>
    /// <see cref="BitmapFilterOperator"/> が実行した <see cref="IPredicate.Evaluate"/> 呼び出し回数。
    /// 述語を選択度順に並べると、後続の述語はページビットマップに残った行だけを評価するため
    /// <c>predicates.Count * rowsIn</c> より少なくなる。
    /// </summary>
    public long PredicateEvaluations;
}
