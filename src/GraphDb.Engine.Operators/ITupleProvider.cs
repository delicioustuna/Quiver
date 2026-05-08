using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public interface ITupleProvider
{
    TupleSlot Provide(in TupleRef current, ITransaction tx);
}
