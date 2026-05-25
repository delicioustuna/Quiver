using System.Buffers.Binary;

namespace Quiver.Wal;

/// <summary>
/// FT-15: <see cref="WalRecordType.PageImage"/> と
/// <see cref="WalRecordType.CompensationLogRecord"/> が共有するページペイロードの
/// エンコード / デコード。両者は「どのファイルのどのページの 8KB バイト列か」という
/// 同一フォーマットを使う (前者は after-image, 後者は before-image)。
///
/// FT-29: ページ末尾の連続ゼロを trim する v2 フォーマットを追加。
/// Quiver の page layout (NodeStore: 32B header + 263 records×31B、BTree: 32B header +
/// EntryCount + 可変長 entries) はデータが先頭側に詰めて配置され末尾は zero で残るため、
/// 末尾ゼロ trim だけで per-tx 1k シナリオの WAL 増幅を 10× 以上削減できる。
///
/// ペイロード形式:
///   v1 (旧、recovery 互換のみ): [version=1:1][fileKind:1][pageId:8][pageBytes:N=8192]
///   v2 (新、writer 標準):       [version=2:1][fileKind:1][pageId:8][usedLen:2][usedBytes:M]
///                                M ≤ 8192、recovery 側で残り (8192-M) バイトを zero-pad して reconstruct。
/// </summary>
public static class WalPageImageCodec
{
    /// <summary>v1 ヘッダ長 (バイト)。version + fileKind + pageId。</summary>
    public const int HeaderLength = 10;

    /// <summary>v2 ヘッダ長 (バイト)。version + fileKind + pageId + usedLen。</summary>
    public const int HeaderLengthV2 = 12;

    /// <summary>復元時のページサイズ (= PagedFile.PageSizeConst)。</summary>
    public const int FullPageBytes = 8192;

    /// <summary>
    /// FT-29: trim で消してはいけない最低保持バイト数。PageHeader (32B) は CRC や magic を持つので
    /// 完全ゼロ判定でも 0 にしない (= 32B 残す)。これにより「未初期化フラグの page」を recovery で
    /// 区別したい場合や、将来 PageHeader.Validate の早期失敗で原因切り分けに使う場合に役立つ。
    /// </summary>
    public const int MinKeptBytes = 32;

    private const byte Version1 = 1;
    private const byte Version2 = 2;

    /// <summary>
    /// ページバイト列を v2 (trim 対応) WAL ペイロードへエンコードする。
    /// 末尾の連続ゼロを切り詰めるため、sparse page では大幅に短くなる。
    /// 完了条件として最低 <see cref="MinKeptBytes"/> バイトは残す (= PageHeader CRC 範囲)。
    /// </summary>
    public static byte[] Encode(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        int lastNonZero = pageBytes.LastIndexOfAnyExcept((byte)0);
        // 全ゼロのページは usedLen=MinKeptBytes として最小ヘッダのみ残す保険。
        int usedLen = lastNonZero < 0 ? MinKeptBytes : Math.Max(lastNonZero + 1, MinKeptBytes);
        if (usedLen > pageBytes.Length) usedLen = pageBytes.Length;

        var payload = new byte[HeaderLengthV2 + usedLen];
        payload[0] = Version2;
        payload[1] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(2), pageId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), (ushort)usedLen);
        pageBytes[..usedLen].CopyTo(payload.AsSpan(HeaderLengthV2));
        return payload;
    }

    /// <summary>
    /// v1 (旧、全ページバイト保持) を書く。テスト・互換用に残す。production からは
    /// 呼ばない (新規 WAL は常に v2)。
    /// </summary>
    public static byte[] EncodeV1(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var payload = new byte[HeaderLength + pageBytes.Length];
        payload[0] = Version1;
        payload[1] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(2), pageId);
        pageBytes.CopyTo(payload.AsSpan(HeaderLength));
        return payload;
    }

    /// <summary>
    /// WAL ペイロードを fileKind / pageId / ページバイト列へ分解する。
    /// v1/v2 両対応。v2 は <see cref="FullPageBytes"/> へ zero-pad した上で <paramref name="pageBytes"/> に返す。
    /// 長さ不足や未対応バージョン、usedLen 不正のときは <c>false</c> を返す。
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out byte fileKind, out long pageId, out byte[] pageBytes)
    {
        fileKind = 0;
        pageId = 0;
        pageBytes = Array.Empty<byte>();
        if (payload.Length < HeaderLength) return false;

        byte version = payload[0];
        if (version == Version1)
        {
            fileKind = payload[1];
            pageId = BinaryPrimitives.ReadInt64LittleEndian(payload[2..]);
            // v1 は全ページバイトをそのまま保持。
            pageBytes = payload[HeaderLength..].ToArray();
            return true;
        }
        if (version == Version2)
        {
            if (payload.Length < HeaderLengthV2) return false;
            fileKind = payload[1];
            pageId = BinaryPrimitives.ReadInt64LittleEndian(payload[2..]);
            int usedLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]);
            if (usedLen > FullPageBytes) return false;
            if (payload.Length < HeaderLengthV2 + usedLen) return false;
            // zero-pad で reconstruct。
            var buf = new byte[FullPageBytes];
            payload.Slice(HeaderLengthV2, usedLen).CopyTo(buf);
            pageBytes = buf;
            return true;
        }
        return false; // 未対応バージョン
    }
}
