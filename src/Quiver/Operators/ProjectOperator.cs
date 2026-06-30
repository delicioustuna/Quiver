using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 上流の各行に対して <see cref="ProjectionSpec"/> の計算式を適用し、新しいスキーマの行を生成するオペレータ。
/// </summary>
internal sealed class ProjectOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly ProjectionSpec[] _projections;
    private ITransaction? _tx;
    private TupleSlot[]? _buffer;
    private TupleSchema? _schema;

    public ProjectOperator(IPhysicalOperator source, ProjectionSpec[] projections)
    {
        _source = source;
        _projections = projections;
    }

    public TupleSchema Schema => _schema ??= new TupleSchema(
        Array.ConvertAll(_projections, p => new ColumnDefinition(p.OutputName, TupleSlotType.Null)));

    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer!);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _buffer = new TupleSlot[_projections.Length];
        _source.Open(tx);
    }

    public bool MoveNext()
    {
        if (!_source.MoveNext()) return false;
        var src = _source.Current;
        for (int i = 0; i < _projections.Length; i++)
            _buffer![i] = _projections[i].Compute.Compute(in src, _tx!);
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() { _source.Dispose(); }
}
