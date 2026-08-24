using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// 単一ラベルに属する <see cref="VertexId"/> を列挙するスキャンオペレータ。
/// </summary>
/// <remarks>
/// 内部で <see cref="IGraphAccessMethods.ScanVertices"/> 経由のアクセスパスに委譲する。
/// バイナリ backend は <c>LabelVertexIndex</c> sidecar を持つため O(|L|) lookup になり、
/// <c>InlineGraphAccessMethods</c> (backend 不在の単体テスト等) は従来の O(N) スキャン
/// + ラベルフィルタへフォールバックする。
/// </remarks>
internal sealed class VertexByLabelScanOperator : IPhysicalOperator
{
    private readonly LabelId _labelId;
    private IEnumerator<VertexId>? _enumerator;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public VertexByLabelScanOperator(LabelId labelId) => _labelId = labelId;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _enumerator = tx.Access.ScanVertices(tx, _labelId).GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_enumerator!.MoveNext())
        {
            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _enumerator.Current.Value };
            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    public void Dispose() => _enumerator?.Dispose();
}
