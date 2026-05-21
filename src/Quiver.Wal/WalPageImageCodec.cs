using System.Buffers.Binary;

namespace Quiver.Wal;

/// <summary>
/// FT-15: <see cref="WalRecordType.PageImage"/> と
/// <see cref="WalRecordType.CompensationLogRecord"/> が共有するページペイロードの
/// エンコード / デコード。両者は「どのファイルのどのページの 8KB バイト列か」という
/// 同一フォーマットを使う (前者は after-image, 後者は before-image)。
///
/// ペイロード形式 (version 1): [version=1:1][fileKind:1][pageId:8][pageBytes:N]
/// </summary>
public static class WalPageImageCodec
{
    /// <summary>version + fileKind + pageId のヘッダ長 (バイト)。</summary>
    public const int HeaderLength = 10;

    /// <summary>ページバイト列を WAL ペイロードへエンコードする。</summary>
    public static byte[] Encode(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var payload = new byte[HeaderLength + pageBytes.Length];
        payload[0] = 1;
        payload[1] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(2), pageId);
        pageBytes.CopyTo(payload.AsSpan(HeaderLength));
        return payload;
    }

    /// <summary>
    /// WAL ペイロードを fileKind / pageId / ページバイト列へ分解する。
    /// 長さ不足や未対応バージョンのときは <c>false</c> を返す。
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out byte fileKind, out long pageId, out ReadOnlySpan<byte> pageBytes)
    {
        fileKind = 0;
        pageId = 0;
        pageBytes = default;
        if (payload.Length < HeaderLength) return false;
        if (payload[0] != 1) return false; // 未対応バージョン
        fileKind = payload[1];
        pageId = BinaryPrimitives.ReadInt64LittleEndian(payload[2..]);
        pageBytes = payload[HeaderLength..];
        return true;
    }
}
