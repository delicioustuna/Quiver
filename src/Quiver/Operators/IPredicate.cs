using Quiver.Transactions;

namespace Quiver.Query.Physical;

internal interface IPredicate
{
    bool Evaluate(in TupleRef tuple, ITransaction tx);
}
