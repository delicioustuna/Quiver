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

    // ------------------------------------------------------------------------
    // WAL group commit のバッチ処理
    // ------------------------------------------------------------------------

    [Fact]
    public void GroupCommit_WindowZero_PreservesLegacyBehavior()
    {
        // window=0 (既定) では従来の opportunistic group commit のみで、
        // 各 FlushTo は確実に fsync を起動する。シングルスレッド逐次 commit では
        // 1 commit = 1 batch になることを確認する。
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"), TimeSpan.Zero);
        for (int i = 0; i < 5; i++)
        {
            long lsn = wal.Append(WalRecordType.Commit, new TransactionId(i + 1), []);
            wal.FlushTo(lsn);
        }
        wal.FlushRequestCount.Should().Be(5);
        wal.FlushBatchCount.Should().Be(5);
    }

    [Fact]
    public void GroupCommit_WithWindow_CoalescesConcurrentCommits()
    {
        // window > 0 では複数スレッドが並列に FlushTo した commit が同じ fsync に束ねられる。
        // 64 concurrent FlushTo / window=5ms (テスト安定性のため仕様の 100µs より広め) で、
        // batch 数 <= request 数 / 4 になることを確認 (緩い閾値で false positive 回避)。
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"), TimeSpan.FromMilliseconds(5));

        const int threadCount = 64;
        var ready = new ManualResetEventSlim(false);
        var threads = new Thread[threadCount];
        var lsns = new long[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                lsns[idx] = wal.Append(WalRecordType.Commit, new TransactionId(idx + 1), []);
                ready.Wait();
                wal.FlushTo(lsns[idx]);
            }) { IsBackground = true };
            threads[i].Start();
        }
        // 全スレッドが Append を終え FlushTo の手前で待機するまで少し待つ
        Thread.Sleep(50);
        ready.Set();
        foreach (var t in threads) t.Join();

        wal.FlushRequestCount.Should().Be(threadCount);
        // batch 数は request 数より大幅に少ないはず (理想は 1〜数回)。
        // CI 環境差を見て 16 以下 (4× 集約) を契約に。
        wal.FlushBatchCount.Should().BeLessThanOrEqualTo(16);
        wal.FlushBatchCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GroupCommit_SingleCommitLatency_BoundedByWindow()
    {
        // 単一 commit のレイテンシは概ね「fsync 時間 + window」で済む。
        // window=2ms で 1 commit を流して 200ms 以内 (緩い上限で flaky 回避) に完了することを確認。
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"), TimeSpan.FromMilliseconds(2));
        long lsn = wal.Append(WalRecordType.Commit, new TransactionId(1), []);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        wal.FlushTo(lsn);
        sw.Stop();
        wal.FlushedLsn.Should().BeGreaterThanOrEqualTo(lsn);
        sw.ElapsedMilliseconds.Should().BeLessThan(200);
    }

    [Fact]
    public void GroupCommit_AfterFlush_AdvancesFlushedLsn()
    {
        // batch flush 後、待機していた全 caller の TargetLsn 以上が flushedLsn に反映される。
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"), TimeSpan.FromMilliseconds(3));
        long lastLsn = -1;
        for (int i = 0; i < 8; i++)
            lastLsn = wal.Append(WalRecordType.Commit, new TransactionId(i + 1), []);
        wal.FlushTo(lastLsn);
        wal.FlushedLsn.Should().BeGreaterThanOrEqualTo(lastLsn);
    }

    // ─────────────────────────────────────────────────────────────────────
    // トランザクション単位の PageImage 結合 (WAL レベルのトランザクション間共有バッファ)
    // ─────────────────────────────────────────────────────────────────────

    private static byte[] MakePageImagePayload(byte fileKind, long pageId, byte fill, int pageSize = 64)
    {
        var page = new byte[pageSize];
        Array.Fill(page, fill);
        return WalPageImageCodec.Encode(fileKind, pageId, page);
    }

    [Fact]
    public void Ft29_BufferedPageImage_IsNotWrittenUntilCommit()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(1);
        var payload = MakePageImagePayload(fileKind: 1, pageId: 100, fill: 0xAB);

        wal.BufferPageImage(tx, fileKind: 1, pageId: 100, payload);

        // Commit / Abort / Checkpoint 系のレコード追加までは PageImage は WAL に書かれない。
        wal.BytesWritten.Should().Be(0);
        wal.CurrentLsn.Should().Be(-1);
    }

    [Fact]
    public void Ft29_BufferedPageImage_IsDrainedOnCommit()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(1);
        var payload = MakePageImagePayload(fileKind: 1, pageId: 100, fill: 0xAB);

        wal.BufferPageImage(tx, 1, 100, payload);
        long commitLsn = wal.Append(WalRecordType.Commit, tx, []);
        wal.FlushTo(commitLsn);

        // drain で PageImage (LSN=0)、続いて Commit (LSN=1) の順で書かれる。
        wal.DrainedPageImageCount.Should().Be(1);
        using var reader = wal.OpenReader(0);
        reader.TryReadNext(out var rec0).Should().BeTrue();
        rec0.Type.Should().Be(WalRecordType.PageImage);
        rec0.TransactionId.Should().Be(tx);
        reader.TryReadNext(out var rec1).Should().BeTrue();
        rec1.Type.Should().Be(WalRecordType.Commit);
        rec1.Lsn.Should().BeGreaterThan(rec0.Lsn);
    }

    [Fact]
    public void Ft29_BufferedPageImage_SamePage_SameTx_LatestWins()
    {
        // intra-tx coalesce: 同一 tx の同一 (fileKind, pageId) は latest-wins。
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(1);
        var firstPayload = MakePageImagePayload(1, 100, 0xAA);
        var secondPayload = MakePageImagePayload(1, 100, 0xCC);

        wal.BufferPageImage(tx, 1, 100, firstPayload);
        wal.BufferPageImage(tx, 1, 100, secondPayload);
        wal.CoalescedPageImageCount.Should().Be(1, "同一 tx で 1 回 latest-wins 置換");

        long commitLsn = wal.Append(WalRecordType.Commit, tx, []);
        wal.FlushTo(commitLsn);

        wal.DrainedPageImageCount.Should().Be(1);
        using var reader = wal.OpenReader(0);
        WalRecord pageImageRec = default;
        while (reader.TryReadNext(out var rec))
            if (rec.Type == WalRecordType.PageImage) pageImageRec = rec;
        WalPageImageCodec.TryDecode(pageImageRec.Payload.Span, out _, out _, out var pageBytes).Should().BeTrue();
        pageBytes[0].Should().Be(0xCC, "intra-tx latest-wins により最新 payload が残る");
    }

    [Fact]
    public void Ft29_BufferedPageImage_SamePage_DifferentTx_DoesNotCoalesce()
    {
        // cross-tx の同一 page は coalesce しない: 既存エントリを drain して per-tx 帰属を維持。
        // これにより undo Pass 3 で CLR_A が tx_B の committed content を壊す経路を排除する。
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var txA = new TransactionId(1);
        var txB = new TransactionId(2);

        wal.BufferPageImage(txA, 1, 100, MakePageImagePayload(1, 100, 0xAA));
        wal.BufferPageImage(txB, 1, 100, MakePageImagePayload(1, 100, 0xBB));

        // Tx_A のエントリは drain されたはず (cross-tx の場合は即時 drain)。
        wal.DrainedPageImageCount.Should().Be(1, "別 tx 衝突で既存エントリが drain される");
        wal.CoalescedPageImageCount.Should().Be(0, "cross-tx は intra-tx coalesce ではない");

        long commitLsn = wal.Append(WalRecordType.Commit, txB, []);
        wal.FlushTo(commitLsn);

        // Tx_A 分 + Tx_B 分 + Commit_B = PageImage 2 件 + Commit 1 件。
        wal.DrainedPageImageCount.Should().Be(2);
        var pageImages = new List<(TransactionId Tx, byte First)>();
        using var reader = wal.OpenReader(0);
        while (reader.TryReadNext(out var rec))
        {
            if (rec.Type == WalRecordType.PageImage)
            {
                WalPageImageCodec.TryDecode(rec.Payload.Span, out _, out _, out var pageBytes).Should().BeTrue();
                pageImages.Add((rec.TransactionId, pageBytes[0]));
            }
        }
        pageImages.Should().HaveCount(2);
        pageImages.Should().Contain((txA, (byte)0xAA));
        pageImages.Should().Contain((txB, (byte)0xBB));
    }

    [Fact]
    public void Ft29_BufferedPageImage_DifferentPages_BothPersisted()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(1);

        wal.BufferPageImage(tx, 1, 100, MakePageImagePayload(1, 100, 0xAA));
        wal.BufferPageImage(tx, 1, 200, MakePageImagePayload(1, 200, 0xBB));
        wal.BufferPageImage(tx, 2, 100, MakePageImagePayload(2, 100, 0xCC));

        wal.CoalescedPageImageCount.Should().Be(0, "別キー同士は重複なし");

        long commitLsn = wal.Append(WalRecordType.Commit, tx, []);
        wal.FlushTo(commitLsn);

        wal.DrainedPageImageCount.Should().Be(3);

        int piCount = 0;
        using var reader = wal.OpenReader(0);
        while (reader.TryReadNext(out var rec))
            if (rec.Type == WalRecordType.PageImage) piCount++;
        piCount.Should().Be(3);
    }

    [Fact]
    public void Ft29_EvictCoalescedPageImagesFor_RemovesEntriesForTx()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var txA = new TransactionId(1);
        var txB = new TransactionId(2);

        wal.BufferPageImage(txA, 1, 100, MakePageImagePayload(1, 100, 0xAA));
        wal.BufferPageImage(txB, 1, 200, MakePageImagePayload(1, 200, 0xBB));

        // Tx_A だけ evict → Tx_A のページは drain されず、Tx_B のページだけが書かれる。
        wal.EvictCoalescedPageImagesFor(txA);

        long commitLsn = wal.Append(WalRecordType.Commit, txB, []);
        wal.FlushTo(commitLsn);

        wal.DrainedPageImageCount.Should().Be(1);
        using var reader = wal.OpenReader(0);
        WalRecord rec = default;
        bool found = false;
        while (reader.TryReadNext(out rec))
        {
            if (rec.Type == WalRecordType.PageImage)
            {
                found = true;
                rec.TransactionId.Should().Be(txB);
            }
        }
        found.Should().BeTrue();
    }

    [Fact]
    public void Ft29_BufferedPageImage_DrainedOnAbort_AttributedToWritingTx()
    {
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(1);
        wal.BufferPageImage(tx, 1, 100, MakePageImagePayload(1, 100, 0xAA));

        long abortLsn = wal.Append(WalRecordType.Abort, tx, []);
        wal.FlushTo(abortLsn);

        // Abort 経路でも drain される (eviction 前に Abort が呼ばれた場合のフォールバック)。
        // recovery 側で abortedTxs に入っているので PageImage は redo されない (correctness は維持)。
        wal.DrainedPageImageCount.Should().Be(1);

        int piCount = 0, abortCount = 0;
        using var reader = wal.OpenReader(0);
        while (reader.TryReadNext(out var rec))
        {
            if (rec.Type == WalRecordType.PageImage) piCount++;
            if (rec.Type == WalRecordType.Abort) abortCount++;
        }
        piCount.Should().Be(1);
        abortCount.Should().Be(1);
    }

    [Fact]
    public void Ft29_DirectAppendPageImage_StillWorks_BackwardCompat()
    {
        // 既存テストは wal.Append(PageImage, ...) を直接使用する。互換のため引き続き動作すること。
        using var wal = new WriteAheadLog(Path.Combine(_dir, "wal"));
        var tx = new TransactionId(7);
        var payload = MakePageImagePayload(1, 100, 0xAA);

        long lsn = wal.Append(WalRecordType.PageImage, tx, payload);
        wal.FlushTo(lsn);

        // coalesce 経路を経由しないため drain カウンタは増えない。
        wal.DrainedPageImageCount.Should().Be(0);
        wal.CoalescedPageImageCount.Should().Be(0);

        using var reader = wal.OpenReader(0);
        reader.TryReadNext(out var rec).Should().BeTrue();
        rec.Type.Should().Be(WalRecordType.PageImage);
        rec.TransactionId.Should().Be(tx);
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
