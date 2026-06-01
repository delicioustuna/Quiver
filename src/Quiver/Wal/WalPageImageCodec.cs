using System.Buffers.Binary;

namespace Quiver.Storage.Wal;

/// <summary>
/// FT-15: <see cref="WalRecordType.PageImage"/> と
/// <see cref="WalRecordType.CompensationLogRecord"/> が共有するページペイロードの
/// エンコード / デコード。両者は「どのファイルのどのページの 8KB バイト列か」という
/// 同一フォーマットを使う (前者は after-image, 後者は before-image)。
///
/// FT-29: ページ末尾の連続ゼロを trim する v2 フォーマットを追加。
/// FT-29b: trim 後の payload を「literal + byte-run」chunk へ符号化する v3 を追加。
/// NodeStore record (31B のうち FF×12 と 00×8 で計 20B が同一バイト run) や
/// PageHeader 直後のページタイプ特有のゼロ run を縮める。
///
/// ペイロード形式 (version dispatch は payload[0] = version byte で判定):
///   v1 (旧): [version=1:1][fileKind:1][pageId:8][pageBytes:N=8192]
///   v2:      [version=2:1][fileKind:1][pageId:8][usedLen:2][usedBytes:M]
///            recovery 側で残り (8192-M) バイトを zero-pad して reconstruct。
///   v3:      [version=3:1][fileKind:1][pageId:8][usedLen:2][chunks...]
///            chunks は逐次連結された可変長ブロックで、各ブロックは:
///              [chunkType:1][...]
///                chunkType=0x00 (Literal): [count:varint(1-3B)][literal_bytes:count]
///                chunkType=0x01 (Run):     [byte_value:1][count:varint(1-3B)]
///            chunks の合計データ長 = usedLen バイト。
///            recovery 側で chunks を decode して先頭 usedLen バイトを構築、残りを zero-pad。
/// </summary>
internal static class WalPageImageCodec
{
    /// <summary>v1 ヘッダ長 (バイト)。version + fileKind + pageId。</summary>
    public const int HeaderLength = 10;

    /// <summary>v2 / v3 ヘッダ長 (バイト)。version + fileKind + pageId + usedLen。</summary>
    public const int HeaderLengthV2 = 12;

    /// <summary>復元時のページサイズ (= PagedFile.PageSizeConst)。</summary>
    public const int FullPageBytes = 8192;

    /// <summary>
    /// FT-29: trim で消してはいけない最低保持バイト数。PageHeader (32B) は CRC や magic を持つので
    /// 完全ゼロ判定でも 0 にしない (= 32B 残す)。これにより「未初期化フラグの page」を recovery で
    /// 区別したい場合や、将来 PageHeader.Validate の早期失敗で原因切り分けに使う場合に役立つ。
    /// </summary>
    public const int MinKeptBytes = 32;

    /// <summary>
    /// FT-29b: RLE chunk 化で「run」と認識する最小連続バイト数。
    /// 損益分岐分析: literal chunk を split して run を挟むコスト = 2 chunk header (4B) + run chunk (3B) -
    /// run bytes。break-even は 7 バイトなので、確実に得をする 8 を採用する。
    /// NodeStore record パターンの FF×12 / 00×8 は両方 trigger される (12B run で 5B 節約、8B run で 1B 節約)。
    /// BTree page の散在する短い run (KeyLen 後ろの 4-6 zero など) は literal のままで余計なオーバヘッドを避ける。
    /// </summary>
    public const int MinRunBytes = 8;

    private const byte Version1 = 1;
    private const byte Version2 = 2;
    private const byte Version3 = 3;
    private const byte ChunkLiteral = 0x00;
    private const byte ChunkRun = 0x01;

    /// <summary>
    /// ページバイト列を v3 (trim + RLE chunk) WAL ペイロードへエンコードする。
    /// 末尾ゼロを trim した上で、連続同一バイトの run (>= <see cref="MinRunBytes"/>) を
    /// chunk として符号化する。NodeStore record の FF×12 / 00×8 等が縮む。
    /// </summary>
    public static byte[] Encode(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        // 1. 末尾ゼロ trim (v2 と同一ロジック)。
        int lastNonZero = pageBytes.LastIndexOfAnyExcept((byte)0);
        int usedLen = lastNonZero < 0 ? MinKeptBytes : Math.Max(lastNonZero + 1, MinKeptBytes);
        if (usedLen > pageBytes.Length) usedLen = pageBytes.Length;

        // 2. trim 後のバイト列を chunk に切り分けて符号化。
        //    "現在の run の先頭位置" を i_runStart で持ち、同一バイトが続く限り進める。
        //    run が MinRunBytes 未満なら literal、それ以上なら Run chunk で出力。
        //    literal は隣接する短い run も含めて 1 chunk にまとめる (chunk header 重複を避ける)。
        var trimmed = pageBytes[..usedLen];

        // 上限見積もり: worst case = 全部 literal (chunk overhead 1+3 = 4 バイト)。
        // 余裕を持って trimmed.Length + 16 を確保。
        var output = new byte[HeaderLengthV2 + trimmed.Length + 16];
        output[0] = Version3;
        output[1] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(2), pageId);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(10), (ushort)usedLen);

        int outPos = HeaderLengthV2;
        int litStart = 0; // 未出力の literal 区間の開始 index

        int i = 0;
        while (i < trimmed.Length)
        {
            // 現在位置から同一バイト run の長さを測る。
            byte b = trimmed[i];
            int runEnd = i + 1;
            while (runEnd < trimmed.Length && trimmed[runEnd] == b) runEnd++;
            int runLen = runEnd - i;

            if (runLen >= MinRunBytes)
            {
                // run の前にある literal 区間をフラッシュ。
                if (i > litStart)
                    outPos = WriteLiteralChunk(output, outPos, trimmed[litStart..i]);
                // Run chunk を書く。
                outPos = WriteRunChunk(output, outPos, b, runLen);
                i = runEnd;
                litStart = i;
            }
            else
            {
                // run が短い → literal 扱い。スキップして進める。
                i = runEnd;
            }
        }
        // 末尾の literal 区間。
        if (litStart < trimmed.Length)
            outPos = WriteLiteralChunk(output, outPos, trimmed[litStart..]);

        // バッファを実サイズに切り詰めて返す。
        if (outPos == output.Length) return output;
        var result = new byte[outPos];
        Buffer.BlockCopy(output, 0, result, 0, outPos);
        return result;
    }

    private static int WriteLiteralChunk(Span<byte> dest, int pos, ReadOnlySpan<byte> bytes)
    {
        dest[pos++] = ChunkLiteral;
        pos = WriteVarint(dest, pos, (uint)bytes.Length);
        bytes.CopyTo(dest[pos..]);
        return pos + bytes.Length;
    }

    private static int WriteRunChunk(Span<byte> dest, int pos, byte value, int count)
    {
        dest[pos++] = ChunkRun;
        dest[pos++] = value;
        pos = WriteVarint(dest, pos, (uint)count);
        return pos;
    }

    // 1-3 バイトの可変長整数 (max 14 ビット = 16383)。ページ内 chunk の count は 8192 が上限なので
    // 14 ビットで足りる。
    private static int WriteVarint(Span<byte> dest, int pos, uint value)
    {
        while (value >= 0x80)
        {
            dest[pos++] = (byte)(value | 0x80);
            value >>= 7;
        }
        dest[pos++] = (byte)value;
        return pos;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> src, ref int pos, out uint value)
    {
        value = 0;
        int shift = 0;
        while (pos < src.Length)
        {
            byte b = src[pos++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 28) return false; // varint が長すぎる (壊れている)
        }
        return false; // 入力が途切れている
    }

    /// <summary>
    /// v1 (旧、全ページバイト保持) を書く。テスト・互換用に残す。production からは
    /// 呼ばない。
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
    /// v2 (trim 単独) を書く。FT-29 で導入した形式。v3 (RLE) が問題ある場合の fallback として残す。
    /// </summary>
    public static byte[] EncodeV2(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        int lastNonZero = pageBytes.LastIndexOfAnyExcept((byte)0);
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
    /// WAL ペイロードを fileKind / pageId / ページバイト列へ分解する。
    /// v1/v2/v3 すべて対応。v2 と v3 は <see cref="FullPageBytes"/> へ zero-pad した上で
    /// <paramref name="pageBytes"/> に返す。長さ不足や未対応バージョン、usedLen / chunk 不正
    /// のときは <c>false</c>。
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
            var buf = new byte[FullPageBytes];
            payload.Slice(HeaderLengthV2, usedLen).CopyTo(buf);
            pageBytes = buf;
            return true;
        }
        if (version == Version3)
        {
            if (payload.Length < HeaderLengthV2) return false;
            fileKind = payload[1];
            pageId = BinaryPrimitives.ReadInt64LittleEndian(payload[2..]);
            int usedLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]);
            if (usedLen > FullPageBytes) return false;

            var buf = new byte[FullPageBytes];
            int writePos = 0;
            int readPos = HeaderLengthV2;
            while (writePos < usedLen)
            {
                if (readPos >= payload.Length) return false;
                byte chunkType = payload[readPos++];
                if (chunkType == ChunkLiteral)
                {
                    if (!TryReadVarint(payload, ref readPos, out uint count)) return false;
                    if (writePos + (int)count > usedLen) return false;
                    if (readPos + (int)count > payload.Length) return false;
                    payload.Slice(readPos, (int)count).CopyTo(buf.AsSpan(writePos));
                    writePos += (int)count;
                    readPos += (int)count;
                }
                else if (chunkType == ChunkRun)
                {
                    if (readPos >= payload.Length) return false;
                    byte value = payload[readPos++];
                    if (!TryReadVarint(payload, ref readPos, out uint count)) return false;
                    if (writePos + (int)count > usedLen) return false;
                    buf.AsSpan(writePos, (int)count).Fill(value);
                    writePos += (int)count;
                }
                else
                {
                    return false; // 未知の chunk type
                }
            }
            if (writePos != usedLen) return false;
            pageBytes = buf;
            return true;
        }
        return false; // 未対応バージョン
    }
}
