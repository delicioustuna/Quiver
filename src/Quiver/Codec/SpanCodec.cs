using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Quiver.Codec;

/// <summary>
/// Span&lt;byte&gt; への型安全なリトルエンディアン読み書きヘルパ。
/// 全メソッドはインライン化され、ゼロアロケーション。
/// </summary>
internal static class SpanCodec
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadByte(ReadOnlySpan<byte> span, int offset) => span[offset];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInt32(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadInt64(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadDouble(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(span[offset..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteByte(Span<byte> span, int offset, byte value) =>
        span[offset] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt32(Span<byte> span, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt64(Span<byte> span, int offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDouble(Span<byte> span, int offset, double value) =>
        BinaryPrimitives.WriteDoubleLittleEndian(span[offset..], value);

    /// <summary>VarInt (LEB128) エンコード。書き込んだバイト数を返す。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteVarInt64(Span<byte> span, int offset, long value)
    {
        // ZigZag エンコード(符号付き対応)
        ulong uv = ZigZagEncode(value);
        int written = 0;
        do
        {
            byte b = (byte)(uv & 0x7F);
            uv >>= 7;
            if (uv != 0) b |= 0x80;
            span[offset + written++] = b;
        }
        while (uv != 0);
        return written;
    }

    /// <summary>VarInt デコード。consumed に消費バイト数を出力。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadVarInt64(ReadOnlySpan<byte> span, int offset, out int consumed)
    {
        ulong result = 0;
        int shift = 0;
        consumed = 0;
        while (true)
        {
            byte b = span[offset + consumed++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return ZigZagDecode(result);
    }

    /// <summary>
    /// VarInt の bounds-checked デコード。truncated / 過長 (shift &gt; 63) のときは
    /// <c>false</c> を返す。fuzz / 信頼できない入力経路で使う安全版。
    /// production の hot path は <see cref="ReadVarInt64"/> を継続使用。
    /// </summary>
    public static bool TryReadVarInt64(
        ReadOnlySpan<byte> span, int offset, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if ((uint)offset > (uint)span.Length) return false;
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            int idx = offset + consumed;
            if ((uint)idx >= (uint)span.Length) return false;
            byte b = span[idx];
            consumed++;
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) return false;
        }
        value = ZigZagDecode(result);
        return true;
    }

    /// <summary>UTF-8 文字列を VarInt 長さプレフィックス付きで書き込む。書き込んだ総バイト数を返す。</summary>
    public static int WriteUtf8(Span<byte> span, int offset, ReadOnlySpan<char> value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        int prefixLen = WriteVarInt64(span, offset, byteCount);
        Encoding.UTF8.GetBytes(value, span[(offset + prefixLen)..]);
        return prefixLen + byteCount;
    }

    /// <summary>UTF-8 文字列本体の byte span を返す。consumed はプレフィックス + 本体の合計バイト数。</summary>
    public static ReadOnlySpan<byte> ReadUtf8Bytes(ReadOnlySpan<byte> span, int offset, out int consumed)
    {
        long byteCount = ReadVarInt64(span, offset, out int prefixLen);
        consumed = prefixLen + (int)byteCount;
        return span.Slice(offset + prefixLen, (int)byteCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ZigZagEncode(long value) =>
        (ulong)((value << 1) ^ (value >> 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ZigZagDecode(ulong value) =>
        (long)(value >> 1) ^ -(long)(value & 1);
}
