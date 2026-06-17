using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// helper for <c>.ShortestPathTo(target)</c>. Wraps an upstream operator and
/// pairs every emitted row's NodeId at <paramref name="sourceColumn"/> with a constant
/// NodeId, producing a fresh 2-column tuple <c>(source, target)</c> that the
/// <see cref="ShortestPathOperator"/> can consume.
/// </summary>
internal sealed class PairWithConstantOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceColumn;
    private readonly long _constantValue;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("source", TupleSlotType.NodeId),
        new ColumnDefinition("target", TupleSlotType.NodeId)]);

    public PairWithConstantOperator(IPhysicalOperator source, int sourceColumn, NodeId constant)
    {
        _source = source;
        _sourceColumn = sourceColumn;
        _constantValue = constant.Sequence; // ARCH-5b: seed の gen を剥がしてパイプラインを Sequence 空間に保つ
    }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) => _source.Open(tx);

    public bool MoveNext()
    {
        if (!_source.MoveNext()) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _source.Current[_sourceColumn].LongValue };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _constantValue };
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() => _source.Dispose();
}
