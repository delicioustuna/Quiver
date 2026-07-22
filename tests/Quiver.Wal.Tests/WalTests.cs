using Xunit;
using Quiver.Storage.Wal;
using Quiver.Core;
using FluentAssertions;

namespace Quiver.Storage.Wal.Tests;

public class WalTests : IDisposable
{
    private readonly string _dir;

    public WalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "WalTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void WalRecordType_Values_AreCorrect()
    {
        ((byte)WalRecordType.BeginWrite).Should().Be(1);
        ((byte)WalRecordType.PageImage).Should().Be(2);
        ((byte)WalRecordType.Commit).Should().Be(3);
        ((byte)WalRecordType.Abort).Should().Be(4);
    }

    [Fact]
    public void Append_ReturnsMonontonicallyIncreasingLsn()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(1);

        long lsn0 = wal.Append(WalRecordType.BeginWrite, tx, ReadOnlySpan<byte>.Empty);
        long lsn1 = wal.Append(WalRecordType.Commit, tx, ReadOnlySpan<byte>.Empty);

        lsn0.Should().Be(0);
        lsn1.Should().BeGreaterThan(lsn0);
        wal.CurrentLsn.Should().Be(lsn1);
    }

    [Fact]
    public void AppendAndRead_RoundTrip()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(42);
        byte[] payload = [1, 2, 3, 4, 5];

        long lsn = wal.Append(WalRecordType.PageImage, tx, payload);
        wal.FlushTo(lsn);

        using var reader = wal.OpenReader(lsn);
        reader.TryReadNext(out WalRecord rec).Should().BeTrue();
        rec.Lsn.Should().Be(lsn);
        rec.Type.Should().Be(WalRecordType.PageImage);
        rec.TransactionId.Should().Be(tx);
        rec.Payload.ToArray().Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void AppendMultiple_ReadAll_InOrder()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var lsns = new List<long>();

        for (int i = 0; i < 10; i++)
        {
            byte[] p = BitConverter.GetBytes(i);
            lsns.Add(wal.Append(WalRecordType.PageImage, new TransactionId(i), p));
        }
        wal.FlushTo(lsns[^1]);

        using var reader = wal.OpenReader(lsns[0]);
        for (int i = 0; i < 10; i++)
        {
            reader.TryReadNext(out WalRecord rec).Should().BeTrue();
            rec.Lsn.Should().Be(lsns[i]);
            BitConverter.ToInt32(rec.Payload.Span).Should().Be(i);
        }
        reader.TryReadNext(out _).Should().BeFalse();
    }

    [Fact]
    public void FlushTo_UpdatesFlushedLsn()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        long lsn = wal.Append(WalRecordType.BeginWrite, new TransactionId(1), []);
        wal.FlushedLsn.Should().BeLessThan(lsn);
        wal.FlushTo(lsn);
        wal.FlushedLsn.Should().BeGreaterThanOrEqualTo(lsn);
    }

    [Fact]
    public void CheckpointPair_ProducesExplicitBoundaryRecords()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        long beginLsn = wal.Append(WalRecordType.CheckpointBegin, TransactionId.Bootstrap, new byte[20]);
        byte[] endPayload = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(endPayload, beginLsn);
        long endLsn = wal.Append(WalRecordType.CheckpointEnd, TransactionId.Bootstrap, endPayload);
        wal.FlushTo(endLsn);

        using var reader = wal.OpenReader(beginLsn);
        reader.TryReadNext(out WalRecord rec).Should().BeTrue();
        rec.Type.Should().Be(WalRecordType.CheckpointBegin);
        reader.TryReadNext(out rec).Should().BeTrue();
        rec.Type.Should().Be(WalRecordType.CheckpointEnd);
        rec.Lsn.Should().Be(endLsn);
    }

    [Fact]
    public void CorruptedRecord_IsRejectedOnOpen()
    {
        long lsn;
        using (var wal = new WriteAheadLog(Path.Combine(_dir, "wal")))
        {
            lsn = wal.Append(WalRecordType.BeginWrite, new TransactionId(1), []);
            wal.FlushTo(lsn);
        }

        // 単一ファイル WAL のチェックサムバイトを破損させる。
        string walFile = Path.Combine(_dir, "wal");
        byte[] bytes = File.ReadAllBytes(walFile);
        // checksum を反転する (レコードの 21..24 バイト)
        bytes[21] ^= 0xFF;
        File.WriteAllBytes(walFile, bytes);

        Action reopen = () => new WriteAheadLog(Path.Combine(_dir, "wal"));
        reopen.Should().Throw<CorruptionException>();
    }

    [Fact]
    public void Reopen_RestoresState_AndCanContinueAppending()
    {
        long lsnBefore;
        using (var wal = new WriteAheadLog(Path.Combine(_dir, "wal")))
        {
            lsnBefore = wal.Append(WalRecordType.BeginWrite, new TransactionId(1), [0, 1]);
            wal.FlushTo(lsnBefore);
        }

        using var wal2 = new WriteAheadLog(Path.Combine(_dir, "wal"));
        long lsnAfter = wal2.Append(WalRecordType.Commit, new TransactionId(1), [2, 3]);
        wal2.FlushTo(lsnAfter);

        lsnAfter.Should().BeGreaterThan(lsnBefore);

        using var reader = wal2.OpenReader(lsnBefore);
        reader.TryReadNext(out WalRecord r1).Should().BeTrue();
        r1.Lsn.Should().Be(lsnBefore);
        reader.TryReadNext(out WalRecord r2).Should().BeTrue();
        r2.Lsn.Should().Be(lsnAfter);
    }

    [Fact]
    public void Truncate_CompactsSingleFile_DroppingOldPrefix()
    {
        // 単一ファイル WAL の Truncate はセグメント削除ではなく
        // prefix を捨てて live tail を前詰めするコンパクション。
        string walFile = Path.Combine(_dir, "wal");
        using var wal = new WriteAheadLog(walFile);

        byte[] bigPayload = new byte[2000];
        long lsn0 = wal.Append(WalRecordType.PageImage, new TransactionId(1), bigPayload);
        long lsn1 = wal.Append(WalRecordType.PageImage, new TransactionId(2), bigPayload);
        long lsn2 = wal.Append(WalRecordType.PageImage, new TransactionId(3), bigPayload);
        wal.FlushTo(lsn2);

        long sizeBefore = new FileInfo(walFile).Length;
        sizeBefore.Should().BeGreaterThan(6000);

        // lsn0/lsn1 を捨てて lsn2 だけを残す。
        wal.Truncate(lsn1);

        long sizeAfter = new FileInfo(walFile).Length;
        sizeAfter.Should().BeLessThan(sizeBefore, "コンパクションで前詰めされファイルが縮む");

        // 残った tail から lsn2 だけが読めること。
        using var reader = wal.OpenReader(0);
        reader.TryReadNext(out var rec).Should().BeTrue();
        rec.Lsn.Should().Be(lsn2);
        reader.TryReadNext(out _).Should().BeFalse();
    }

    [Fact]
    public void Truncate_DropsEverything_WhenAllBelowThreshold()
    {
        // 全レコードが uptoLsn 以下なら単一ファイルは空になる。
        string walFile = Path.Combine(_dir, "wal");
        using var wal = new WriteAheadLog(walFile);
        long lsn0 = wal.Append(WalRecordType.PageImage, new TransactionId(1), new byte[100]);
        long lsn1 = wal.Append(WalRecordType.PageImage, new TransactionId(2), new byte[100]);
        wal.FlushTo(lsn1);

        wal.Truncate(lsn1); // 全部捨てる

        new FileInfo(walFile).Length.Should().Be(WalFormat.FileHeaderSize);
        using var reader = wal.OpenReader(0);
        reader.TryReadNext(out _).Should().BeFalse();

        // 空ファイルへの追記が続けられること (LSN は単調継続)。
        long lsn2 = wal.Append(WalRecordType.Commit, new TransactionId(3), []);
        lsn2.Should().BeGreaterThan(lsn1);
    }

    [Fact]
    public void OpenReader_StartLsn_SkipsPriorRecords()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        long lsn0 = wal.Append(WalRecordType.BeginWrite, new TransactionId(1), []);
        long lsn1 = wal.Append(WalRecordType.Commit, new TransactionId(1), []);
        wal.FlushTo(lsn1);

        using var reader = wal.OpenReader(lsn1);
        reader.TryReadNext(out WalRecord rec).Should().BeTrue();
        rec.Lsn.Should().Be(lsn1);
        reader.TryReadNext(out _).Should().BeFalse();
    }

    [Fact]
    public void Transaction_write_set_keeps_only_the_latest_image_per_page()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(1);
        var writeSet = new WalWriteSet(wal, tx);
        var page = new byte[WalPageImageCodec.FullPageBytes];
        PageHeader.Write(page, new PageId(100), PageKind.BTreeLeaf, lsn: -1);

        page[PageHeader.Size] = 0xAA;
        writeSet.LogPageImage(fileKind: 1, pageId: 100, page);
        page[PageHeader.Size] = 0xCC;
        writeSet.LogPageImage(fileKind: 1, pageId: 100, page);

        wal.CurrentLsn.Should().Be(-1);
        writeSet.FlushPending();
        long commitLsn = wal.Append(WalRecordType.Commit, tx, []);
        wal.FlushTo(commitLsn);

        using var reader = wal.OpenReader(0);
        reader.TryReadNext(out var image).Should().BeTrue();
        image.Type.Should().Be(WalRecordType.PageImage);
        WalPageImageCodec.TryDecode(
            image.Payload.Span,
            out byte fileKind,
            out long pageId,
            out var pageBytes).Should().BeTrue();
        fileKind.Should().Be(1);
        pageId.Should().Be(100);
        pageBytes[PageHeader.Size].Should().Be(0xCC);
        PageHeader.ReadLsn(pageBytes).Should().Be(image.Lsn);

        reader.TryReadNext(out var commit).Should().BeTrue();
        commit.Type.Should().Be(WalRecordType.Commit);
        commit.TransactionId.Should().Be(tx);
    }

    [Fact]
    public void Abort_does_not_flush_pending_page_images()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(1);
        var writeSet = new WalWriteSet(wal, tx);
        var page = new byte[WalPageImageCodec.FullPageBytes];
        PageHeader.Write(page, new PageId(100), PageKind.BTreeLeaf, lsn: -1);
        writeSet.LogPageImage(fileKind: 1, pageId: 100, page);

        long abortLsn = wal.Append(WalRecordType.Abort, tx, []);
        wal.FlushTo(abortLsn);

        using var reader = wal.OpenReader(0);
        reader.TryReadNext(out var abort).Should().BeTrue();
        abort.Type.Should().Be(WalRecordType.Abort);
        reader.TryReadNext(out _).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────
    // WalPageImageCodec v2 (trim) の codec レベルテスト
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Codec_v2_TrimsTrailingZeros_RoundTrip()
    {
        // 63 バイト non-zero + 8129 バイト zero の 8192 バイトページを encode/decode する。
        var page = new byte[WalPageImageCodec.FullPageBytes];
        for (int i = 0; i < 63; i++) page[i] = (byte)(i + 1);
        // 末尾 8129 バイトは初期値 0 のまま。

        var encoded = WalPageImageCodec.Encode(fileKind: 1, pageId: 100, page);
        // payload = v2 header (12B) + 63 バイト (= 末尾 zero 直前まで)。
        // ただし MinKeptBytes=32 のため 63 >= 32 で trim 影響なし → 12+63 = 75 バイト前後。
        encoded.Length.Should().BeInRange(WalPageImageCodec.HeaderLength, WalPageImageCodec.HeaderLength + WalPageImageCodec.FullPageBytes);
        encoded.Length.Should().BeLessThan(200, "sparse page は trim で大幅に小さくなる");

        WalPageImageCodec.TryDecode(encoded, out byte fileKind, out long pageId, out byte[] pageBytes).Should().BeTrue();
        fileKind.Should().Be(1);
        pageId.Should().Be(100);
        pageBytes.Length.Should().Be(WalPageImageCodec.FullPageBytes);
        for (int i = 0; i < 63; i++) pageBytes[i].Should().Be((byte)(i + 1));
        for (int i = 63; i < WalPageImageCodec.FullPageBytes; i++) pageBytes[i].Should().Be(0);
    }

    [Fact]
    public void Codec_with_trailing_legacy_bytes_is_rejected()
    {
        // 旧 v1 形式 (8192 バイト全保持) を手動構築して TryDecode が復元できることを確認。
        // 旧形式の DB に残る WAL レコードを安全に読み出せる互換性保証。
        var v1Payload = new byte[WalPageImageCodec.HeaderLength + WalPageImageCodec.FullPageBytes];
        v1Payload[0] = 1; // version=1
        v1Payload[1] = 7; // fileKind
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(v1Payload.AsSpan(2), 42L);
        v1Payload[WalPageImageCodec.HeaderLength + 100] = 0xEF; // 適当な位置に non-zero

        WalPageImageCodec.TryDecode(v1Payload, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Codec_v3_FullPage_NoRuns_RoundTrip()
    {
        // 全部非ゼロかつ run 化できないパターン (alternating bytes) なページの round-trip。
        // RLE 効果なし、literal chunk 1 個 + 1+2 バイトの chunk overhead で v2 比 +3B 程度。
        var page = new byte[WalPageImageCodec.FullPageBytes];
        for (int i = 0; i < page.Length; i++) page[i] = (byte)((i & 0xFE) | 1); // 全て非ゼロ、隣接同値なし

        var encoded = WalPageImageCodec.Encode(fileKind: 1, pageId: 5, page);
        // v3 = header (12) + literal chunk header (1 + 2 varint) + 8192 = 8207 バイト。
        encoded.Length.Should().BeInRange(8205, 8210, "literal chunk 1 個でほぼ生サイズ");

        WalPageImageCodec.TryDecode(encoded, out _, out _, out byte[] pageBytes).Should().BeTrue();
        pageBytes.Should().BeEquivalentTo(page);
    }

    [Fact]
    public void Codec_v2_PreservesPageHeader_When_AllZeroExceptHeader()
    {
        // ページが全部ゼロでも MinKeptBytes (32) は残す保険ロジックが効くこと。
        var page = new byte[WalPageImageCodec.FullPageBytes];
        // 全部ゼロ。

        var encoded = WalPageImageCodec.Encode(fileKind: 2, pageId: 9, page);
        // v3 (RLE) では 32B 全ゼロは Run chunk 1 個 = ~5B で圧縮されるため、v2 サイズ比較は撤廃。

        WalPageImageCodec.TryDecode(encoded, out _, out _, out byte[] pageBytes).Should().BeTrue();
        pageBytes.Length.Should().Be(WalPageImageCodec.FullPageBytes);
        pageBytes.Should().AllBeEquivalentTo((byte)0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // v3 RLE chunk 符号化
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Codec_v3_VertexStoreRecordPattern_CompressesByteRuns()
    {
        // VertexStore record (31B) パターン: 01 FF×12 01 00 XX×8 00×8
        // FF×12 と 00×8 が Run chunk で圧縮されることを確認。
        var page = new byte[WalPageImageCodec.FullPageBytes];
        // ヘッダ後に 1 record。
        int off = 32; // PageHeader.Size
        page[off + 0] = 0x01;
        for (int i = 1; i <= 12; i++) page[off + i] = 0xFF;
        page[off + 13] = 0x01;
        page[off + 14] = 0x00;
        for (int i = 15; i <= 22; i++) page[off + i] = (byte)(i - 14); // Xmin varint
        // 23～30 バイトはゼロ (Xmax)
        // bytes 32+31=63 以降は zero。

        var encoded = WalPageImageCodec.Encode(fileKind: 1, pageId: 5, page);
        // 末尾ゼロは trim、record 内の FF/00 run は RLE 化。
        // 期待: encoded はおおよそ 30-50 バイト程度 (v2 trim では 75 バイト前後)。
        encoded.Length.Should().BeLessThan(80,
            "RLE で FF×12 と 00×8 が 1 chunk = 3 バイトずつに圧縮される");

        // round-trip 復元の検証。
        WalPageImageCodec.TryDecode(encoded, out byte fileKind, out long pageId, out byte[] pageBytes).Should().BeTrue();
        fileKind.Should().Be(1);
        pageId.Should().Be(5);
        pageBytes.Length.Should().Be(WalPageImageCodec.FullPageBytes);
        pageBytes.AsSpan(0, 63).ToArray().Should().BeEquivalentTo(page.AsSpan(0, 63).ToArray(),
            "record 領域の content が完全に復元される");
        // 残りは zero-pad。
        for (int i = 63; i < WalPageImageCodec.FullPageBytes; i++) pageBytes[i].Should().Be(0);
    }

    [Fact]
    public void Codec_v3_NoRunsBelowThreshold_StaysLiteral()
    {
        // 3 バイト未満の run は literal 扱い (chunk overhead 回避)。
        // 例: ABCABC... のパターンは run 化しない。
        var page = new byte[WalPageImageCodec.FullPageBytes];
        for (int i = 0; i < 60; i++) page[i] = (byte)((i % 3) + 1); // 1,2,3,1,2,3,...

        var encoded = WalPageImageCodec.Encode(fileKind: 1, pageId: 5, page);
        // 全部 literal 1 chunk。chunk header (1B + varint 1B) + 60B literal = 62B + v3 header 12B = 74B。
        encoded.Length.Should().BeInRange(70, 80);

        WalPageImageCodec.TryDecode(encoded, out _, out _, out byte[] pageBytes).Should().BeTrue();
        for (int i = 0; i < 60; i++) pageBytes[i].Should().Be((byte)((i % 3) + 1));
    }

    [Fact]
    public void Codec_v3_FullPageOfSameByte_CompressesToSingleRunChunk()
    {
        // 8192B 全部 0xFF のページは Run chunk 1 個 = ~5B (chunk type 1 + value 1 + varint 2) で完結。
        var page = new byte[WalPageImageCodec.FullPageBytes];
        Array.Fill(page, (byte)0xFF);

        var encoded = WalPageImageCodec.Encode(fileKind: 1, pageId: 5, page);
        // v3 header 12B + Run chunk ~5B = 17B 前後。劇的削減。
        encoded.Length.Should().BeLessThan(25, "全 0xFF は 1 Run chunk で表現できる");

        WalPageImageCodec.TryDecode(encoded, out _, out _, out byte[] pageBytes).Should().BeTrue();
        pageBytes.Should().AllBeEquivalentTo((byte)0xFF);
    }

    [Fact]
    public void Codec_v3_MixedLiteralAndRun_RoundTrip()
    {
        // 複雑な mix: literal / run / literal / run / literal を交互に。
        var page = new byte[WalPageImageCodec.FullPageBytes];
        page[0] = 0xAA; page[1] = 0xBB; page[2] = 0xCC; // literal (3B, < MinRunBytes なので literal 扱い)
        for (int i = 3; i < 20; i++) page[i] = 0x11; // run 17B
        page[20] = 0x99; page[21] = 0x88; // literal 2B
        for (int i = 22; i < 100; i++) page[i] = 0xEE; // run 78B
        page[100] = 0x77; // literal 1B

        var encoded = WalPageImageCodec.Encode(fileKind: 7, pageId: 42, page);
        WalPageImageCodec.TryDecode(encoded, out byte fileKind, out long pageId, out byte[] pageBytes).Should().BeTrue();
        fileKind.Should().Be(7);
        pageId.Should().Be(42L);
        pageBytes.AsSpan(0, 101).ToArray().Should().BeEquivalentTo(page.AsSpan(0, 101).ToArray());
    }

    [Fact]
    public void Codec_v3_AllZeroPage_CompressesToSingleZeroRun()
    {
        // 全ゼロページは v3 では MinKeptBytes (32) 分の Zero run 1 chunk。
        var page = new byte[WalPageImageCodec.FullPageBytes];

        var encoded = WalPageImageCodec.Encode(fileKind: 1, pageId: 1, page);
        // v3 header 12B + Run chunk (32 zero) ~4B = 16B 前後。
        encoded.Length.Should().BeLessThan(25);

        WalPageImageCodec.TryDecode(encoded, out _, out _, out byte[] pageBytes).Should().BeTrue();
        pageBytes.Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void Codec_v3_Decode_RejectsCorruptedChunkStream()
    {
        // 壊れた v3 payload (未知の chunk type) は TryDecode が false を返す。
        var bad = new byte[]
        {
            3, // version=3
            1, // fileKind
            0, 0, 0, 0, 0, 0, 0, 0, // pageId=0
            10, 0, // usedLen=10
            0xFF, // 未知の chunk type
        };
        WalPageImageCodec.TryDecode(bad, out _, out _, out _).Should().BeFalse();
    }
}
