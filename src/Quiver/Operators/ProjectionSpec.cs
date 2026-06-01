using Quiver.Transactions;

namespace Quiver.Query.Physical;

// TupleRef は ref struct のため Func<> の型引数に使えない。専用インタフェースで代替する。
internal interface IProjectionCompute
{
    TupleSlot Compute(in TupleRef tuple, ITransaction tx);
}

internal sealed class ProjectionSpec
{
    public string OutputName { get; }
    public IProjectionCompute Compute { get; }

    public ProjectionSpec(string outputName, IProjectionCompute compute)
    {
        OutputName = outputName;
        Compute = compute;
    }
}
