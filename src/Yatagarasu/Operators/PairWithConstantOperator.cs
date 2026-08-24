using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// <c>.ShortestPathTo(target)</c> 用のヘルパー。上流オペレータの各行の VertexId に定数
/// VertexId を組み合わせ、<see cref="ShortestPathOperator"/> が消費する 2 列タプル
/// <c>(source, target)</c> を生成する。
/// </summary>
internal sealed class PairWithConstantOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceColumn;
    private readonly VertexId _constant;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("source", TupleSlotType.VertexId),
        new ColumnDefinition("target", TupleSlotType.VertexId)]);

    public PairWithConstantOperator(IPhysicalOperator source, int sourceColumn, VertexId constant)
    {
        _source = source;
        _sourceColumn = sourceColumn;
        _constant = constant;
    }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) => _source.Open(tx);

    public bool MoveNext()
    {
        if (!_source.MoveNext()) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _source.Current[_sourceColumn].LongValue };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _constant.Value };
        var s = Statistics; s.RowsProduced++; Statistics = s;
        return true;
    }

    public void Dispose() => _source.Dispose();
}
