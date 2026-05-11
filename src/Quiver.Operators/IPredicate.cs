using Quiver.Transactions;

namespace Quiver.Operators;

public interface IPredicate
{
    bool Evaluate(in TupleRef tuple, ITransaction tx);
}
