using System.Text;
using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>コンパイル時に確定した定数値を返す <see cref="ITupleProvider"/>。</summary>
internal sealed class LiteralProvider : ITupleProvider
{
    private readonly TupleSlotType _type;
    private readonly long _scalar;
    private readonly byte[]? _bytes;

    private LiteralProvider(TupleSlotType type, long scalar, byte[]? bytes)
    {
        _type = type; _scalar = scalar; _bytes = bytes;
    }

    public TupleSlotType SlotType => _type;

    public TupleSlot Provide(in TupleRef current, ITransaction tx) => _type switch
    {
        TupleSlotType.Utf8String or TupleSlotType.Bytes => new TupleSlot
            { Type = _type, BytesOffset = 0, BytesLength = _bytes?.Length ?? 0 },
        _ => new TupleSlot { Type = _type, LongValue = _scalar },
    };

    public ReadOnlySpan<byte> ProvideBytes(in TupleRef current, ITransaction tx)
        => _bytes ?? ReadOnlySpan<byte>.Empty;

    public static LiteralProvider Int64(long v) => new(TupleSlotType.Int64, v, null);
    public static LiteralProvider Double(double v) => new(TupleSlotType.Double, BitConverter.DoubleToInt64Bits(v), null);
    public static LiteralProvider Bool(bool v) => new(TupleSlotType.Bool, v ? 1L : 0L, null);
    public static LiteralProvider String(string s) => new(TupleSlotType.Utf8String, 0, Encoding.UTF8.GetBytes(s));
    public static LiteralProvider Bytes(byte[] b) => new(TupleSlotType.Bytes, 0, b);
    // クエリパイプラインは Sequence 空間で動く。利用者 seed の gen を入口で剥がす。
    public static LiteralProvider VertexId(VertexId id) => new(TupleSlotType.VertexId, id.Sequence, null);
}
