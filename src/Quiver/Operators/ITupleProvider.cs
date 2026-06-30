using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>現在のタプル行から 1 スロット分の値を算出するプロバイダ。</summary>
internal interface ITupleProvider
{
    TupleSlot Provide(in TupleRef current, ITransaction tx);
    TupleSlotType SlotType { get; }
    ReadOnlySpan<byte> ProvideBytes(in TupleRef current, ITransaction tx) => ReadOnlySpan<byte>.Empty;
}
