using System.Buffers.Binary;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// ARCH-5c Phase 3: node version payload 内の inline property 領域の符号化/復号。
/// VersionedNodeStore (set/remove/scan) と <see cref="PropertyEnumerator"/> (列挙) で共用する。
///
/// <para>payload レイアウト: 固定 15B (flags/firstRel/firstProp/label) + [15] inlineCount(u8) +
/// entries。entry = <c>[keyId:4][type:1][len:1][value:len]</c>。len は u8 のため値長 ≤ 255。
/// それを超える string/bytes は inline 不可で overflow チェーン (PropertyStore) 行き。</para>
/// </summary>
internal static class InlinePropertyCodec
{
    public const int FixedSize = 15;       // NodeWriteHandle が触る固定フィールド領域
    public const int OffInlineCount = 15;  // u8
    public const int BaseSize = 16;        // 固定 + inlineCount (inline 0 件時の payload 長)
    public const int EntryHeader = 6;      // keyId(4) + type(1) + len(1)
    public const int ValueMax = 255;       // len は u8

    public static int Count(ReadOnlySpan<byte> payload)
        => payload.Length > OffInlineCount ? payload[OffInlineCount] : 0;

    public static bool IsInlineable(in PropertyValue v) => v.EncodedSize <= ValueMax;

    /// <summary>pos (byte offset) の entry を読み、次の pos を返す。</summary>
    public static (int KeyId, PropertyValueType Type, int NextPos) ReadEntryHeader(ReadOnlySpan<byte> payload, int pos)
    {
        int keyId = BinaryPrimitives.ReadInt32LittleEndian(payload[pos..]);
        var type = (PropertyValueType)payload[pos + 4];
        int len = payload[pos + 5];
        return (keyId, type, pos + EntryHeader + len);
    }

    public static ReadOnlySpan<byte> ValueAt(ReadOnlySpan<byte> payload, int pos)
        => payload.Slice(pos + EntryHeader, payload[pos + 5]);

    public static bool TryScan(ReadOnlySpan<byte> payload, int keyId, out PropertyValueType type, out ReadOnlySpan<byte> val)
    {
        type = default; val = default;
        int count = Count(payload);
        int pos = BaseSize;
        for (int i = 0; i < count; i++)
        {
            int k = BinaryPrimitives.ReadInt32LittleEndian(payload[pos..]);
            int len = payload[pos + 5];
            if (k == keyId)
            {
                type = (PropertyValueType)payload[pos + 4];
                val = payload.Slice(pos + EntryHeader, len);
                return true;
            }
            pos += EntryHeader + len;
        }
        return false;
    }

    public static PropertyValue Decode(PropertyValueType type, ReadOnlySpan<byte> val) => type switch
    {
        PropertyValueType.Bool => PropertyValue.FromBool(val[0] != 0),
        PropertyValueType.Int32 => PropertyValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(val)),
        PropertyValueType.Int64 => PropertyValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(val)),
        PropertyValueType.Double => PropertyValue.FromDouble(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(val))),
        PropertyValueType.String => PropertyValue.FromUtf8(val),
        PropertyValueType.Bytes => PropertyValue.FromBytes(val),
        _ => throw new CorruptionException($"unknown inline property type {type}"),
    };

    private static void WriteValue(Span<byte> dst, in PropertyValue v)
    {
        switch (v.Type)
        {
            case PropertyValueType.Bool: dst[0] = v.BoolValue ? (byte)1 : (byte)0; break;
            case PropertyValueType.Int32: BinaryPrimitives.WriteInt32LittleEndian(dst, v.Int32Value); break;
            case PropertyValueType.Int64: BinaryPrimitives.WriteInt64LittleEndian(dst, v.Int64Value); break;
            case PropertyValueType.Double: BinaryPrimitives.WriteInt64LittleEndian(dst, BitConverter.DoubleToInt64Bits(v.DoubleValue)); break;
            case PropertyValueType.String: v.Utf8StringValue.CopyTo(dst); break;
            case PropertyValueType.Bytes: v.BytesValue.CopyTo(dst); break;
        }
    }

    /// <summary>cur payload を基に keyId の entry を set/remove した新 payload を作る (固定 15B 保持)。</summary>
    public static byte[] Build(ReadOnlySpan<byte> cur, int keyId, in PropertyValue value, bool remove)
    {
        int count = Count(cur);
        int keepBytes = 0, keepCount = 0;
        int pos = BaseSize;
        for (int i = 0; i < count; i++)
        {
            int k = BinaryPrimitives.ReadInt32LittleEndian(cur[pos..]);
            int entrySize = EntryHeader + cur[pos + 5];
            if (k != keyId) { keepBytes += entrySize; keepCount++; }
            pos += entrySize;
        }
        int vlen = remove ? 0 : value.EncodedSize;
        int total = BaseSize + keepBytes + (remove ? 0 : EntryHeader + vlen);
        var buf = new byte[total];
        cur.Slice(0, FixedSize).CopyTo(buf);
        buf[OffInlineCount] = (byte)(keepCount + (remove ? 0 : 1));

        int w = BaseSize;
        pos = BaseSize;
        for (int i = 0; i < count; i++)
        {
            int k = BinaryPrimitives.ReadInt32LittleEndian(cur[pos..]);
            int entrySize = EntryHeader + cur[pos + 5];
            if (k != keyId) { cur.Slice(pos, entrySize).CopyTo(buf.AsSpan(w)); w += entrySize; }
            pos += entrySize;
        }
        if (!remove)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(w), keyId);
            buf[w + 4] = (byte)value.Type;
            buf[w + 5] = (byte)vlen;
            WriteValue(buf.AsSpan(w + EntryHeader, vlen), in value);
        }
        return buf;
    }
}
