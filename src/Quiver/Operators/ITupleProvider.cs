using Quiver.Transactions;

namespace Quiver.Query.Physical;

public interface ITupleProvider
{
    TupleSlot Provide(in TupleRef current, ITransaction tx);
    TupleSlotType SlotType { get; }
    ReadOnlySpan<byte> ProvideBytes(in TupleRef current, ITransaction tx) => ReadOnlySpan<byte>.Empty;
}
