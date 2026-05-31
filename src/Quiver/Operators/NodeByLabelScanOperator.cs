using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 単一ラベルに属する <see cref="NodeId"/> を列挙するスキャンオペレータ。
/// </summary>
/// <remarks>
/// VEC-11: 内部で <see cref="IGraphAccessMethods.ScanNodes"/> 経由のアクセスパスに委譲する。
/// バイナリ backend は <c>LabelNodeIndex</c> sidecar を持つため O(|L|) lookup になり、
/// <c>InlineGraphAccessMethods</c> (backend 不在の単体テスト等) は従来の O(N) スキャン
/// + ラベルフィルタにフォールバックする (legacy fallback)。
/// </remarks>
public sealed class NodeByLabelScanOperator : IPhysicalOperator
{
    private readonly LabelId _labelId;
    private IEnumerator<NodeId>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public NodeByLabelScanOperator(LabelId labelId) => _labelId = labelId;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _enumerator = tx.Access.ScanNodes(tx, _labelId).GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator!.MoveNext())
        {
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _enumerator.Current.Value };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose() => _enumerator?.Dispose();
}
