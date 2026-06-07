using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>定数 1 ノードを起点として 1 行だけ放出する物理オペレータ (<c>g.Node(id)</c>)。</summary>
internal sealed class SingleNodeOperator : IPhysicalOperator
{
    private readonly NodeId _nodeId;
    private bool _done;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    internal SingleNodeOperator(NodeId nodeId) { _nodeId = nodeId; }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _done = false; }

    public bool MoveNext()
    {
        if (_done) return false;
        _done = true;
        // ARCH-5b: seed の gen を剥がしてパイプラインを Sequence 空間に保つ。
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _nodeId.Sequence };
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() { }
}

/// <summary>定数 N ノードを起点として各 1 行を放出する物理オペレータ (<c>g.Nodes(ids)</c>)。</summary>
internal sealed class MultiNodeOperator : IPhysicalOperator
{
    private readonly NodeId[] _nodeIds;
    private int _index;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    internal MultiNodeOperator(NodeId[] nodeIds) { _nodeIds = nodeIds; }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _index = 0; }

    public bool MoveNext()
    {
        if (_index >= _nodeIds.Length) return false;
        // ARCH-5b: seed の gen を剥がす。
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _nodeIds[_index++].Sequence };
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() { }
}
