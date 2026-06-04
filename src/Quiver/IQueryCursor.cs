using Quiver.Query.Physical;

namespace Quiver;

/// <summary>
/// 物理プランの結果を1行ずつ遅延消費するカーソル。
/// MoveNext() を呼ぶたびに Current が更新される。
/// Current は次の MoveNext() 呼び出しまでのみ有効。
/// </summary>
internal interface IQueryCursor : IDisposable
{
    /// <summary>カーソルが返すタプルのスキーマ。</summary>
    TupleSchema Schema { get; }

    /// <summary>次の行に進む。行が無くなったら <c>false</c>。</summary>
    bool MoveNext();

    /// <summary>直近の <see cref="MoveNext"/> で取得した現在行。次回呼び出しまでのみ有効。</summary>
    QueryRow Current { get; }
}

internal sealed class PhysicalOperatorCursor : IQueryCursor
{
    private readonly IPhysicalOperator _plan;
    private readonly Quiver.Storage.Records.INodeStore _nodes;
    private QueryRow _current;

    internal PhysicalOperatorCursor(IPhysicalOperator plan, Quiver.Storage.Records.INodeStore nodes)
    {
        _plan = plan;
        _nodes = nodes;
    }

    public TupleSchema Schema => _plan.Schema;

    public bool MoveNext()
    {
        if (!_plan.MoveNext()) return false;
        var cur = _plan.Current;
        var slots = new TupleSlot[cur.ColumnCount];
        byte[]?[]? byteData = null;
        for (int i = 0; i < cur.ColumnCount; i++)
        {
            slots[i] = cur[i];
            if (slots[i].Type is TupleSlotType.Utf8String or TupleSlotType.Bytes)
            {
                byteData ??= new byte[]?[cur.ColumnCount];
                byteData[i] = _plan.GetBytes(i).ToArray();
            }
        }
        // ARCH-5b: 結果 NodeId 列に現世代を load (round-trip 一貫)。
        QueryRowMaterializer.StampNodeGenerations(slots, _nodes);
        _current = new QueryRow(slots, byteData);
        return true;
    }

    public QueryRow Current => _current;

    public void Dispose() => _plan.Dispose();
}
