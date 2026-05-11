using Quiver.Operators;

namespace Quiver;

/// <summary>
/// 物理プランの結果を1行ずつ遅延消費するカーソル。
/// MoveNext() を呼ぶたびに Current が更新される。
/// Current は次の MoveNext() 呼び出しまでのみ有効。
/// </summary>
public interface IQueryCursor : IDisposable
{
    TupleSchema Schema { get; }
    bool MoveNext();
    QueryRow Current { get; }
}

internal sealed class PhysicalOperatorCursor : IQueryCursor
{
    private readonly IPhysicalOperator _plan;
    private QueryRow _current;

    internal PhysicalOperatorCursor(IPhysicalOperator plan) => _plan = plan;

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
        _current = new QueryRow(slots, byteData);
        return true;
    }

    public QueryRow Current => _current;

    public void Dispose() => _plan.Dispose();
}
