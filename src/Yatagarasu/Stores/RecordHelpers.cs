using System.Buffers.Binary;

namespace Yatagarasu.Storage.Records;

internal static class RecordHelpers
{
    public static long ReadInt48(ReadOnlySpan<byte> src)
    {
        ulong lo = BinaryPrimitives.ReadUInt32LittleEndian(src);
        ulong hi = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]);
        ulong raw = lo | (hi << 32);
        return (raw & 0x0000_8000_0000_0000UL) != 0
            ? (long)(raw | 0xFFFF_0000_0000_0000UL)
            : (long)raw;
    }

    public static void WriteInt48(Span<byte> dst, long value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, (uint)(value & 0xFFFF_FFFFL));
        BinaryPrimitives.WriteUInt16LittleEndian(dst[4..], (ushort)((ulong)value >> 32 & 0xFFFF));
    }

    public static long ReadInt40(ReadOnlySpan<byte> src)
    {
        ulong lo = BinaryPrimitives.ReadUInt32LittleEndian(src);
        ulong hi = src[4];
        ulong raw = lo | (hi << 32);
        // bit 39 を符号拡張: 負の場合は上位 24 bit を 1 にする
        return (raw & 0x80_0000_0000UL) != 0
            ? (long)(raw | 0xFFFF_FF00_0000_0000UL)
            : (long)raw;
    }

    public static void WriteInt40(Span<byte> dst, long value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, (uint)(value & 0xFFFF_FFFFL));
        dst[4] = (byte)((ulong)value >> 32 & 0xFF);
    }
}
