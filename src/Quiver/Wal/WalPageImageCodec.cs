using System.Buffers.Binary;

namespace Quiver.Storage.Wal;

/// <summary>
/// <see cref="WalRecordType.PageImage"/> のページペイロード codec。
/// ページ末尾の連続ゼロを trim し、残りを「literal + byte-run」chunk へ符号化する。
/// VertexStore record (31B のうち FF×12 と 00×8 で計 20B が同一バイト run) や
/// PageHeader 直後のページタイプ特有のゼロ run を縮める。
///
/// ペイロード形式:
///   [familyVersion=1:1][fileKind:1][pageId:8][usedLen:2][chunks...]
///            chunks は逐次連結された可変長ブロックで、各ブロックは:
///              [chunkType:1][...]
///                chunkType=0x00 (Literal): [count:varint(1-3B)][literal_bytes:count]
///                chunkType=0x01 (Run):     [byte_value:1][count:varint(1-3B)]
///            chunks の合計データ長 = usedLen バイト。
///            recovery 側で chunks を decode して先頭 usedLen バイトを構築、残りを zero-pad。
/// </summary>
internal static class WalPageImageCodec
{
    /// <summary>family version + fileKind + pageId + usedLen の長さ。</summary>
    public const int HeaderLength = 12;

    /// <summary>復元時のページサイズ (= PagedFile.PageSizeConst)。</summary>
    public const int FullPageBytes = 8192;

    /// <summary>
    /// trim で消してはいけない最低保持バイト数。PageHeader は checksum と magic を持つので
    /// 完全ゼロ判定でも除去しない。これにより「未初期化フラグの page」を recovery で
    /// 区別したい場合や、将来 PageHeader.Validate の早期失敗で原因切り分けに使う場合に役立つ。
    /// </summary>
    public const int MinKeptBytes = Quiver.Storage.PageHeader.Size;

    /// <summary>
    /// RLE chunk 化で「run」と認識する最小連続バイト数。
    /// 損益分岐分析: literal chunk を split して run を挟むコスト = 2 chunk header (4B) + run chunk (3B) -
    /// run bytes。break-even は 7 バイトなので、確実に得をする 8 を採用する。
    /// VertexStore record パターンの FF×12 / 00×8 は両方 trigger される (12B run で 5B 節約、8B run で 1B 節約)。
    /// BTree page の散在する短い run (KeyLen 後ろの 4-6 zero など) は literal のままで余計なオーバヘッドを避ける。
    /// </summary>
    public const int MinRunBytes = 8;

    private const byte PayloadVersion = 1;
    private const byte ChunkLiteral = 0x00;
    private const byte ChunkRun = 0x01;

    /// <summary>
    /// ページバイト列を QUIVER-SW family version 1 の WAL ペイロードへエンコードする。
    /// 末尾ゼロを trim した上で、連続同一バイトの run (>= <see cref="MinRunBytes"/>) を
    /// chunk として符号化する。VertexStore record の FF×12 / 00×8 等が縮む。
    /// </summary>
    public static byte[] Encode(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        // 1. 末尾の連続ゼロを trim する。
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
        var output = new byte[HeaderLength + trimmed.Length + 16];
        output[0] = PayloadVersion;
        output[1] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(2), pageId);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(10), (ushort)usedLen);

        int outPos = HeaderLength;
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
    /// WAL ペイロードを fileKind / pageId / ページバイト列へ分解する。
    /// 長さ不足、family version 不一致、usedLen または chunk 不正のときは <c>false</c>。
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out byte fileKind, out long pageId, out byte[] pageBytes)
    {
        fileKind = 0;
        pageId = 0;
        pageBytes = Array.Empty<byte>();
        if (payload.Length < HeaderLength) return false;

        if (payload[0] != PayloadVersion) return false;

        fileKind = payload[1];
        pageId = BinaryPrimitives.ReadInt64LittleEndian(payload[2..]);
        int usedLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]);
        if (usedLen > FullPageBytes) return false;

        var buf = new byte[FullPageBytes];
        int writePos = 0;
        int readPos = HeaderLength;
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
                return false;
            }
        }

        if (writePos != usedLen || readPos != payload.Length) return false;
        pageBytes = buf;
        return true;
    }
}
