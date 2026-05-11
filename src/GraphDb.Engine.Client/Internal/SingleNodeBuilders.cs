using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Client.Internal;

/// <summary>CorrelatedInputOperator を IOperatorBuilder として包む。SubTraversal の起点に使用する。</summary>
internal sealed class CorrelatedSeedBuilder : IOperatorBuilder
{
    private readonly CorrelatedInputOperator _probe;
    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal CorrelatedSeedBuilder(CorrelatedInputOperator probe) { _probe = probe; }

    public IPhysicalOperator Build(ISchemaApi schema) => _probe;
}


internal sealed class SingleNodeBuilder : IOperatorBuilder
{
    private readonly NodeId _nodeId;
    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal SingleNodeBuilder(NodeId nodeId) { _nodeId = nodeId; }

    public IPhysicalOperator Build(ISchemaApi schema) => new SingleNodeOperator(_nodeId);
}

internal sealed class MultiNodeBuilder : IOperatorBuilder
{
    private readonly NodeId[] _nodeIds;
    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal MultiNodeBuilder(NodeId[] nodeIds) { _nodeIds = nodeIds; }

    public IPhysicalOperator Build(ISchemaApi schema) => new MultiNodeOperator(_nodeIds);
}

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
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _nodeId.Value };
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() { }
}

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
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _nodeIds[_index++].Value };
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() { }
}
