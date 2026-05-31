using Quiver.Transactions;

namespace Quiver.Operators;

// TupleRef は ref struct のため Func<> の型引数に使えない。専用インタフェースで代替する。
public interface IProjectionCompute
{
    TupleSlot Compute(in TupleRef tuple, ITransaction tx);
}

public sealed class ProjectionSpec
{
    public string OutputName { get; }
    public IProjectionCompute Compute { get; }

    public ProjectionSpec(string outputName, IProjectionCompute compute)
    {
        OutputName = outputName;
        Compute = compute;
    }
}
