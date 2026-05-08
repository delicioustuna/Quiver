using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public sealed class ProjectOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly ProjectionSpec[] _projections;
    private ITransaction? _tx;
    private TupleSlot[]? _buffer;

    public ProjectOperator(IPhysicalOperator source, ProjectionSpec[] projections)
    {
        _source = source;
        _projections = projections;
    }

    public TupleSchema Schema => throw new NotImplementedException();
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer!);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _buffer = new TupleSlot[_projections.Length];
        _source.Open(tx);
    }

    public bool MoveNext() => throw new NotImplementedException();
    public void Dispose() { _source.Dispose(); }
}
