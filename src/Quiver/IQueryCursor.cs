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
    private readonly Quiver.Storage.Records.IRelationshipStore _relationships;
    private readonly Quiver.Storage.Records.IHyperedgeStore _hyperedges;
    private QueryRow _current;
    // A-sub: per-row 確保を避けるため slots / byteData バッファを 1 度確保して再利用する。
    // IQueryCursor.Current は「次の MoveNext までのみ有効」契約 (TraversalCursor が即座に
    // 値型へ射影してコピーアウトする) ため、行をまたいだバッファ上書きは安全。
    // ※ materialize 経路 (GraphTransaction.Execute) は各行を保持するので別実装 (プールしない)。
    private TupleSlot[]? _slots;
    private byte[]?[]? _byteData;

    internal PhysicalOperatorCursor(
        IPhysicalOperator plan,
        Quiver.Storage.Records.INodeStore nodes,
        Quiver.Storage.Records.IRelationshipStore relationships,
        Quiver.Storage.Records.IHyperedgeStore hyperedges)
    {
        _plan = plan;
        _nodes = nodes;
        _relationships = relationships;
        _hyperedges = hyperedges;
    }

    public TupleSchema Schema => _plan.Schema;

    public bool MoveNext()
    {
        if (!_plan.MoveNext()) return false;
        var cur = _plan.Current;
        int n = cur.ColumnCount;
        var slots = _slots;
        if (slots is null || slots.Length != n)
            slots = _slots = new TupleSlot[n];

        byte[]?[]? byteData = null;
        for (int i = 0; i < n; i++)
        {
            slots[i] = cur[i];
            if (slots[i].Type is TupleSlotType.Utf8String or TupleSlotType.Bytes)
            {
                if (byteData is null)
                {
                    byteData = _byteData;
                    if (byteData is null || byteData.Length != n)
                        byteData = _byteData = new byte[]?[n];
                    // この行に string/bytes 列がある以上、再利用バッファを一旦クリアして
                    // 前行の残骸 (例: OPTIONAL で今行は Null になった列) を残さない。
                    Array.Clear(byteData, 0, n);
                }
                byteData[i] = _plan.GetBytes(i).ToArray();
            }
        }
        // 結果 NodeId 列に現世代を load (round-trip 一貫)。
        QueryRowMaterializer.StampEntityGenerations(slots, _nodes, _relationships, _hyperedges);
        _current = new QueryRow(slots, byteData);
        return true;
    }

    public QueryRow Current => _current;

    public void Dispose() => _plan.Dispose();
}
