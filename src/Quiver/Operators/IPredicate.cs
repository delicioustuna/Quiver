using Quiver.Transactions;

namespace Quiver.Query.Physical;

public interface IPredicate
{
    bool Evaluate(in TupleRef tuple, ITransaction tx);
}
