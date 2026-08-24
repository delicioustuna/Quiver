namespace Yatagarasu.FuzzTests;

/// <summary>
/// 各 fuzz target 用のインライン seed 入力。コミット時点で必ず通る基本ケース。
/// regressions/ ディレクトリと corpus/ ディレクトリは追加分用 (filesystem ベース)。
/// 「インライン seed = 全 target の base ケース」「filesystem corpus = 拡張・nightly 育成」
/// の二段構成で、初期コミットでも `dotnet test` が意味のある fuzz walk を回せるようにする。
/// </summary>
internal static class InlineSeeds
{
    public static readonly byte[][] WalRecord =
    [
        [],
        new byte[1],
        new byte[24],
        new byte[25],
        Enumerable.Repeat((byte)0xFF, 32).ToArray(),
        Enumerable.Repeat((byte)0x00, 64).ToArray(),
        // 長さフィールド (先頭 4B LE) が極端な値: 0, int.MinValue, int.MaxValue
        [0, 0, 0, 0, .. new byte[64]],
        [0xFF, 0xFF, 0xFF, 0x7F, .. new byte[32]],
        [0x00, 0x00, 0x00, 0x80, .. new byte[32]],
        // header だけ存在し payload が無いケース
        [25, 0, 0, 0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 1, 0,0,0,0],
        // length が header 未満 (24) — 即 reject
        [24, 0, 0, 0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 1, 0,0,0,0],
        // length が MaxPayloadSize 超過
        [0xFF, 0xFF, 0xFF, 0x7F],
        // ランダムなバイト列
        [0x13, 0x37, 0xCA, 0xFE, 0xBA, 0xBE, 0xDE, 0xAD, 0xBE, 0xEF],
    ];

    public static readonly byte[][] WalPageImage =
    [
        [],
        [1],
        [2],
        [3],
        [99],
        // 現行ヘッダーより短い入力
        new byte[WalPageImageCodecLayout.HeaderLength - 1],
        // usedLen が page size を超える
        BuildHeader(usedLen: 0xFFFF, fileKind: 1, pageId: 0),
        // usedLen == 8192 だが payload がない
        BuildHeader(usedLen: 8192, fileKind: 1, pageId: 0),
        // chunk が切り詰められた入力
        BuildTruncatedChunk(),
        // 未知の chunk type を持つ入力
        BuildUnknownChunk(),
        // usedLen は 100 だが単一 literal が 200 バイトを宣言する入力
        BuildLiteralOverrun(),
    ];

    public static readonly byte[][] SpanCodec =
    [
        [],
        [0x00],
        [0x80],
        [0xFF],
        // 10 連続の 0x80 — 過長 varint
        Enumerable.Repeat((byte)0x80, 10).ToArray(),
        // 11 連続 (shift > 63 で reject 期待)
        Enumerable.Repeat((byte)0x80, 11).ToArray(),
        new byte[64],
        // ZigZag 各種境界
        [0x01, 0x02, 0x04, 0x80, 0x01, 0xFF, 0x7F],
    ];

    private static byte[] BuildHeader(int usedLen, byte fileKind, long pageId)
    {
        var buf = new byte[WalPageImageCodecLayout.HeaderLength];
        buf[0] = 1;
        buf[1] = fileKind;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2), pageId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            buf.AsSpan(10), (ushort)Math.Clamp(usedLen, 0, 0xFFFF));
        return buf;
    }

    private static byte[] BuildTruncatedChunk()
    {
        // familyVersion=1、fileKind=1、pageId=0、usedLen=64 に続いて、
        // 長さ 64 を宣言する単一 literal chunk header があるが、後続バイトはない。
        var buf = new byte[WalPageImageCodecLayout.HeaderLength + 2];
        buf[0] = 1;
        buf[1] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), 64);
        buf[12] = 0x00; // chunk = literal
        buf[13] = 64;   // varint count = 64
        return buf;
    }

    private static byte[] BuildUnknownChunk()
    {
        var buf = new byte[WalPageImageCodecLayout.HeaderLength + 1];
        buf[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), 1);
        buf[12] = 0x7E; // 未知 chunk
        return buf;
    }

    private static byte[] BuildLiteralOverrun()
    {
        var buf = new byte[WalPageImageCodecLayout.HeaderLength + 4 + 50];
        buf[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10), 100);
        buf[12] = 0x00;
        // varint = 200 (2 バイト)
        buf[13] = 0xC8;
        buf[14] = 0x01;
        return buf;
    }
}

/// <summary>codec 内定数を test 側で再現するためのシンボル。</summary>
internal static class WalPageImageCodecLayout
{
    public const int HeaderLength = 12;
}
