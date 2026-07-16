using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>定数 1 Vertexを起点として 1 行だけ放出する物理オペレータ (<c>g.Vertex(id)</c>)。</summary>
internal sealed class SingleVertexOperator : IPhysicalOperator
{
    private readonly VertexId _vertexId;
    private bool _done;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    internal SingleVertexOperator(VertexId vertexId) { _vertexId = vertexId; }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _done = false; }

    public bool MoveNext()
    {
        if (_done) return false;
        _done = true;
        // seed の gen を剥がしてパイプラインを Sequence 空間に保つ。
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _vertexId.Sequence };
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() { }
}

/// <summary>定数 N Vertexを起点として各 1 行を放出する物理オペレータ (<c>g.Vertices(ids)</c>)。</summary>
internal sealed class MultiVertexOperator : IPhysicalOperator
{
    private readonly VertexId[] _vertexIds;
    private int _index;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    internal MultiVertexOperator(VertexId[] vertexIds) { _vertexIds = vertexIds; }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _index = 0; }

    public bool MoveNext()
    {
        if (_index >= _vertexIds.Length) return false;
        // seed の gen を剥がす。
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _vertexIds[_index++].Sequence };
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// 定数Nexusを起点として 1 行だけ放出する物理オペレータ。
/// メンバー展開側がスナップショット可視性を検証するため、ここでは ID の受け渡しだけを行う。
/// </summary>
internal sealed class SingleNexusOperator : IPhysicalOperator
{
    private readonly NexusId _nexusId;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];
    private bool _done;

    internal SingleNexusOperator(NexusId nexusId) => _nexusId = nexusId;

    public TupleSchema Schema { get; } =
        new([new ColumnDefinition("nexusId", TupleSlotType.NexusId)]);

    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) => _done = false;

    public bool MoveNext()
    {
        if (_done) return false;
        _done = true;
        _buffer[0] = new TupleSlot
        {
            Type = TupleSlotType.NexusId,
            LongValue = _nexusId.Value,
        };
        var statistics = Statistics;
        statistics.RowsProduced++;
        Statistics = statistics;
        return true;
    }

    public void Dispose() { }
}
