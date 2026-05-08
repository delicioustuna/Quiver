using GraphDb.Engine.Transactions;

namespace GraphDb.Engine.Operators;

public interface ITupleProvider
{
    TupleSlot Provide(in TupleRef current, ITransaction tx);
    TupleSlotType SlotType { get; }
    ReadOnlySpan<byte> ProvideBytes(in TupleRef current, ITransaction tx) => ReadOnlySpan<byte>.Empty;
}
