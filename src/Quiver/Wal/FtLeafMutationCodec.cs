using System.Buffers.Binary;

namespace Quiver.Storage.Wal;

/// <summary>
/// FTS-7: <see cref="WalRecordType.FtLeafMutation"/> のペイロード codec (design 13 §10.2)。
/// postings/norms B+Tree leaf への 1 件の <b>state-setting</b> 論理ミューテーションを表す。
///
/// ペイロード形式:
/// <code>
///   [op:1] [indexTenantId:1] [keyLen:2 LE] [keyBytes:keyLen] [value:8 LE]
///     op            = 1: Upsert / 2: Delete
///     indexTenantId = postings or norms の tenant byte (logical FT 索引の識別; §10.8)
///     keyBytes      = postings: PostingsKey (term+entityId) / norms: entityId(8B BE)
///     value         = Upsert: 新 tf / docLen   Delete: 削除した旧 tf / docLen (undo 再挿入用)
/// </code>
///
/// 冪等性 (design 13 §10.2 不変条件): redo は「set key=value」「delete key」の state-setting で
/// 二重適用が no-op。1 レコードで redo と undo の双方を賄う (Upsert↔Delete、value に旧値を載せる)。
/// </summary>
internal static class FtLeafMutationCodec
{
    /// <summary>論理ミューテーションの種別。</summary>
    public enum Op : byte
    {
        Upsert = 1,
        Delete = 2,
    }

    private const int HeaderLen = 1 + 1 + 2; // op + tenant + keyLen

    /// <summary>ペイロードをエンコードする。</summary>
    public static byte[] Encode(Op op, byte indexTenantId, ReadOnlySpan<byte> key, long value)
    {
        if (key.Length > ushort.MaxValue)
            throw new ArgumentException($"FtLeafMutation key too long: {key.Length}", nameof(key));
        var buf = new byte[HeaderLen + key.Length + 8];
        buf[0] = (byte)op;
        buf[1] = indexTenantId;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)key.Length);
        key.CopyTo(buf.AsSpan(HeaderLen));
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(HeaderLen + key.Length), value);
        return buf;
    }

    /// <summary>ペイロードをデコードする。長さ不正なら <c>false</c>。</summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out Op op, out byte indexTenantId, out ReadOnlySpan<byte> key, out long value)
    {
        op = default;
        indexTenantId = 0;
        key = default;
        value = 0;
        if (payload.Length < HeaderLen) return false;
        op = (Op)payload[0];
        indexTenantId = payload[1];
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        if (payload.Length < HeaderLen + keyLen + 8) return false;
        key = payload.Slice(HeaderLen, keyLen);
        value = BinaryPrimitives.ReadInt64LittleEndian(payload[(HeaderLen + keyLen)..]);
        return true;
    }
}
