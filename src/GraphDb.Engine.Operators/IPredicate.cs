using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public interface IPredicate
{
    bool Evaluate(in TupleRef tuple, ITransaction tx);
}
