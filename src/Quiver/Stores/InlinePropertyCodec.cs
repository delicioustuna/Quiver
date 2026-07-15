using System.Buffers.Binary;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// entity version payload 内の inline property 領域の符号化/復号。
/// <see cref="VersionedNodeStore"/> / <see cref="VersionedRelationshipStore"/> (set/remove/scan) と
/// <see cref="PropertyEnumerator"/> (列挙) で共用する。
///
/// <para>payload レイアウト: 固定 <c>fixedSize</c> B (entity ごとの構造フィールド) +
/// [fixedSize] inlineCount(u8) + entries。entry = <c>[keyId:4][type:1][len:1][value:len]</c>。
/// len は u8 のため値長 ≤ 255。それを超える string/bytes は inline 不可で overflow チェーン
/// (PropertyStore) 行き。fixedSize は node=15 (flags/firstRel/firstProp/label) /
/// rel=45 (flags/source/target/type/4本chain pointer/firstProp) /
/// hyperedge=15 (flags/type/firstIncidence/firstProp)。</para>
/// </summary>
internal static class InlinePropertyCodec
{
    /// <summary>node version payload の固定フィールド領域長 (flags/firstRel/firstProp/label)。</summary>
    public const int NodeFixedSize = 15;
    /// <summary>rel version payload の固定フィールド領域長 (flags/source/target/type/4本chain/firstProp)。</summary>
    public const int RelFixedSize = 45;
    /// <summary>hyperedge header payload の固定フィールド領域長 (flags/type/firstIncidence/firstProp)。</summary>
    public const int HyperedgeFixedSize = 15;
    public const int EntryHeader = 6;      // keyId(4) + type(1) + len(1)
    public const int ValueMax = 255;       // len は u8

    /// <summary>inlineCount(u8) の byte offset。</summary>
    public static int OffInlineCount(int fixedSize) => fixedSize;
    /// <summary>inline 0 件時の payload 長 (固定 + inlineCount)。entries はここから始まる。</summary>
    public static int BaseSize(int fixedSize) => fixedSize + 1;

    public static int Count(ReadOnlySpan<byte> payload, int fixedSize)
        => payload.Length > fixedSize ? payload[fixedSize] : 0;

    public static bool IsInlineable(in PropertyValue v) => v.EncodedSize <= ValueMax;

    /// <summary>
    /// scalar 型 (Bool/Int32/Int64/Double) は <see cref="PropertyValue"/> に値をコピーして保持する
    /// (span を握らない) ため、stackalloc バッファ上で decode しても返した値が dangle しない。
    /// String/Bytes は span を握るので安定メモリ (byte[]) が要る。
    /// </summary>
    public static bool IsScalar(PropertyValueType t)
        => t is PropertyValueType.Bool or PropertyValueType.Int32
             or PropertyValueType.Int64 or PropertyValueType.Double;

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

    public static bool TryScan(ReadOnlySpan<byte> payload, int fixedSize, int keyId, out PropertyValueType type, out ReadOnlySpan<byte> val)
    {
        type = default; val = default;
        int count = Count(payload, fixedSize);
        int pos = BaseSize(fixedSize);
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

    /// <summary>
    /// scalar 値を decode する。<c>scoped</c> 入力で、返す <see cref="PropertyValue"/> は span を
    /// 握らず値をコピーするため、呼出側の stackalloc バッファ上の span を渡しても安全 (escape しない)。
    /// String/Bytes は span を握るのでこのメソッドでは扱わない (<see cref="Decode"/> + 安定 byte[] を使う)。
    /// </summary>
    public static PropertyValue DecodeScalar(PropertyValueType type, scoped ReadOnlySpan<byte> val) => type switch
    {
        PropertyValueType.Bool => PropertyValue.FromBool(val[0] != 0),
        PropertyValueType.Int32 => PropertyValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(val)),
        PropertyValueType.Int64 => PropertyValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(val)),
        PropertyValueType.Double => PropertyValue.FromDouble(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(val))),
        _ => throw new CorruptionException($"non-scalar inline property type {type}"),
    };

    public static PropertyValue Decode(PropertyValueType type, ReadOnlySpan<byte> val) => type switch
    {
        PropertyValueType.Bool => PropertyValue.FromBool(val[0] != 0),
        PropertyValueType.Int32 => PropertyValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(val)),
        PropertyValueType.Int64 => PropertyValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(val)),
        PropertyValueType.Double => PropertyValue.FromDouble(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(val))),
        PropertyValueType.String => PropertyValue.FromUtf8(val),
        PropertyValueType.Bytes => PropertyValue.FromBytes(val),
        PropertyValueType.FloatArray => PropertyValue.FromFloatArray(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(val)),
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
            case PropertyValueType.FloatArray: System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.FloatArrayValue).CopyTo(dst); break;
        }
    }

    /// <summary>cur payload を基に keyId の entry を set/remove した新 payload を作る (固定 fixedSize B 保持)。</summary>
    public static byte[] Build(ReadOnlySpan<byte> cur, int fixedSize, int keyId, in PropertyValue value, bool remove)
    {
        int baseSize = BaseSize(fixedSize);
        int count = Count(cur, fixedSize);
        int keepBytes = 0, keepCount = 0;
        int pos = baseSize;
        for (int i = 0; i < count; i++)
        {
            int k = BinaryPrimitives.ReadInt32LittleEndian(cur[pos..]);
            int entrySize = EntryHeader + cur[pos + 5];
            if (k != keyId) { keepBytes += entrySize; keepCount++; }
            pos += entrySize;
        }
        int vlen = remove ? 0 : value.EncodedSize;
        int total = baseSize + keepBytes + (remove ? 0 : EntryHeader + vlen);
        var buf = new byte[total];
        cur.Slice(0, fixedSize).CopyTo(buf);
        buf[OffInlineCount(fixedSize)] = (byte)(keepCount + (remove ? 0 : 1));

        int w = baseSize;
        pos = baseSize;
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
