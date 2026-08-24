using System.Buffers.Binary;
using System.Text;

namespace Yatagarasu.Index;

internal sealed class Int32KeyCodec : IKeyCodec<int>
{
    public int GetEncodedSize(in int key) => 4;
    public void Encode(in int key, Span<byte> dest)
    {
        // 符号ビットを反転して unsigned 辞書順を正しくする
        BinaryPrimitives.WriteUInt32BigEndian(dest, (uint)key ^ 0x8000_0000u);
    }
    public int Decode(ReadOnlySpan<byte> src)
        => (int)(BinaryPrimitives.ReadUInt32BigEndian(src) ^ 0x8000_0000u);
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}

internal sealed class Int64KeyCodec : IKeyCodec<long>
{
    public int GetEncodedSize(in long key) => 8;
    public void Encode(in long key, Span<byte> dest)
    {
        BinaryPrimitives.WriteUInt64BigEndian(dest, (ulong)key ^ 0x8000_0000_0000_0000uL);
    }
    public long Decode(ReadOnlySpan<byte> src)
        => (long)(BinaryPrimitives.ReadUInt64BigEndian(src) ^ 0x8000_0000_0000_0000uL);
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}

internal sealed class DoubleKeyCodec : IKeyCodec<double>
{
    public int GetEncodedSize(in double key) => 8;
    public void Encode(in double key, Span<byte> dest)
    {
        // IEEE 754 ビットパターン: 符号ビットは常に反転、負数なら全ビット反転
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(key);
        ulong encoded = ((long)bits < 0) ? ~bits : bits ^ 0x8000_0000_0000_0000uL;
        BinaryPrimitives.WriteUInt64BigEndian(dest, encoded);
    }
    public double Decode(ReadOnlySpan<byte> src)
    {
        ulong encoded = BinaryPrimitives.ReadUInt64BigEndian(src);
        ulong bits = ((long)encoded < 0) ? encoded ^ 0x8000_0000_0000_0000uL : ~encoded;
        return BitConverter.Int64BitsToDouble((long)bits);
    }
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}

internal sealed class StringKeyCodec : IKeyCodec<string>
{
    public int GetEncodedSize(in string key) => Encoding.UTF8.GetByteCount(key);
    public void Encode(in string key, Span<byte> dest) => Encoding.UTF8.GetBytes(key, dest);
    public string Decode(ReadOnlySpan<byte> src) => Encoding.UTF8.GetString(src);
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}

internal sealed class BytesKeyCodec : IKeyCodec<byte[]>
{
    public int GetEncodedSize(in byte[] key) => key.Length;
    public void Encode(in byte[] key, Span<byte> dest) => key.AsSpan().CopyTo(dest);
    public byte[] Decode(ReadOnlySpan<byte> src) => src.ToArray();
    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}
