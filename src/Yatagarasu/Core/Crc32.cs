using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Yatagarasu.Core;

/// <summary>
/// 永続ページ形式と WAL 形式で使用する IEEE CRC-32 実装。
/// </summary>
/// <remarks>
/// 反転 IEEE 802.3 多項式 <c>0xEDB88320</c> を使用する。
/// ページは独立した 2 個の CRC 値を XOR し、WAL はヘッダとペイロードを
/// incremental に計算するため、one-shot と incremental の両方を提供する。
/// </remarks>
internal sealed class Crc32
{
    private const uint InitialState = uint.MaxValue;
    private const uint Polynomial = 0xEDB8_8320u;
    private static readonly uint[] Tables = CreateTables();

    private uint _state = InitialState;

    public static uint HashToUInt32(ReadOnlySpan<byte> source)
    {
        uint state = InitialState;
        AppendCore(ref state, source);
        return ~state;
    }

    public void Append(ReadOnlySpan<byte> source) => AppendCore(ref _state, source);

    public uint GetCurrentHashAsUInt32() => ~_state;

    public void Reset() => _state = InitialState;

    private static void AppendCore(ref uint state, ReadOnlySpan<byte> source)
    {
        uint crc = state;
        int offset = 0;

        while (source.Length - offset >= 16)
        {
            uint first = crc ^ BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
            crc =
                Table(15, (byte)first) ^
                Table(14, (byte)(first >> 8)) ^
                Table(13, (byte)(first >> 16)) ^
                Table(12, (byte)(first >> 24)) ^
                Table(11, source[offset + 4]) ^
                Table(10, source[offset + 5]) ^
                Table(9, source[offset + 6]) ^
                Table(8, source[offset + 7]) ^
                Table(7, source[offset + 8]) ^
                Table(6, source[offset + 9]) ^
                Table(5, source[offset + 10]) ^
                Table(4, source[offset + 11]) ^
                Table(3, source[offset + 12]) ^
                Table(2, source[offset + 13]) ^
                Table(1, source[offset + 14]) ^
                Table(0, source[offset + 15]);
            offset += 16;
        }

        for (; offset < source.Length; offset++)
            crc = Table(0, (byte)(crc ^ source[offset])) ^ (crc >> 8);

        state = crc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Table(int slice, byte index) => Tables[(slice << 8) + index];

    private static uint[] CreateTables()
    {
        var tables = new uint[16 * 256];
        for (int i = 0; i < 256; i++)
        {
            uint value = (uint)i;
            for (int bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) != 0 ? Polynomial : 0);
            tables[i] = value;
        }

        for (int slice = 1; slice < 16; slice++)
        {
            int previous = (slice - 1) << 8;
            int current = slice << 8;
            for (int i = 0; i < 256; i++)
            {
                uint value = tables[previous + i];
                tables[current + i] = (value >> 8) ^ tables[(byte)value];
            }
        }

        return tables;
    }
}
