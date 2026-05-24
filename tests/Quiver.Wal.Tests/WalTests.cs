using Xunit;
using Quiver.Wal;
using Quiver.Core;
using FluentAssertions;

namespace Quiver.Wal.Tests;

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
        ((byte)WalRecordType.Begin).Should().Be(1);
        ((byte)WalRecordType.Commit).Should().Be(2);
        ((byte)WalRecordType.EndOfSegment).Should().Be(0xFE);
    }

    [Fact]
    public void Append_ReturnsMonontonicallyIncreasingLsn()
    {
        using var wal = new WriteAheadLog(_dir);
        var tx = new TransactionId(1);

        long lsn0 = wal.Append(WalRecordType.Begin, tx, ReadOnlySpan<byte>.Empty);
        long lsn1 = wal.Append(WalRecordType.Commit, tx, ReadOnlySpan<byte>.Empty);

        lsn0.Should().Be(0);
        lsn1.Should().BeGreaterThan(lsn0);
        wal.CurrentLsn.Should().Be(lsn1);
    }

    [Fact]
    public void AppendAndRead_RoundTrip()
    {
        using var wal = new WriteAheadLog(_dir);
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
        using var wal = new WriteAheadLog(_dir);
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
        using var wal = new WriteAheadLog(_dir);
        long lsn = wal.Append(WalRecordType.Begin, new TransactionId(1), []);
        wal.FlushedLsn.Should().BeLessThan(lsn);
        wal.FlushTo(lsn);
        wal.FlushedLsn.Should().BeGreaterThanOrEqualTo(lsn);
    }

    [Fact]
    public void WriteCheckpoint_ProducesCheckpointRecord()
    {
        using var wal = new WriteAheadLog(_dir);
        long cpLsn = wal.WriteCheckpoint(oldestActiveLsn: 0, lastFlushedDataLsn: 0);

        using var reader = wal.OpenReader(cpLsn);
        reader.TryReadNext(out WalRecord rec).Should().BeTrue();
        rec.Type.Should().Be(WalRecordType.Checkpoint);
        rec.Lsn.Should().Be(cpLsn);
    }

    [Fact]
    public void CorruptedRecord_IsSkipped_ReturnsNoMore()
    {
        long lsn;
        using (var wal = new WriteAheadLog(_dir))
        {
            lsn = wal.Append(WalRecordType.Begin, new TransactionId(1), []);
            wal.FlushTo(lsn);
        }

        // Corrupt the checksum bytes in the segment file
        string segFile = Path.Combine(_dir, "wal.00000000.log");
        byte[] bytes = File.ReadAllBytes(segFile);
        // Flip the checksum (bytes 21..24 of the record)
        bytes[21] ^= 0xFF;
        File.WriteAllBytes(segFile, bytes);

        using var wal2 = new WriteAheadLog(_dir);
        using var reader = wal2.OpenReader(0);
        reader.TryReadNext(out _).Should().BeFalse();
    }

    [Fact]
    public void Reopen_RestoresState_AndCanContinueAppending()
    {
        long lsnBefore;
        using (var wal = new WriteAheadLog(_dir))
        {
            lsnBefore = wal.Append(WalRecordType.Begin, new TransactionId(1), [0, 1]);
            wal.FlushTo(lsnBefore);
        }

        using var wal2 = new WriteAheadLog(_dir);
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
    public void Truncate_RemovesOldSegments()
    {
        // Use tiny segment size so we roll quickly
        using var wal = new WriteAheadLog(_dir, segmentCapacity: 256);

        byte[] bigPayload = new byte[200];
        long lsn0 = wal.Append(WalRecordType.PageImage, new TransactionId(1), bigPayload);
        long lsn1 = wal.Append(WalRecordType.PageImage, new TransactionId(2), bigPayload);
        wal.FlushTo(lsn1);

        // Should have at least 2 segment files now
        int segsBefore = Directory.GetFiles(_dir, "wal.????????.log").Length;
        segsBefore.Should().BeGreaterThan(1);

        wal.Truncate(lsn0);

        int segsAfter = Directory.GetFiles(_dir, "wal.????????.log").Length;
        segsAfter.Should().BeLessThan(segsBefore);
    }

    [Fact]
    public void OpenReader_StartLsn_SkipsPriorRecords()
    {
        using var wal = new WriteAheadLog(_dir);
        long lsn0 = wal.Append(WalRecordType.Begin, new TransactionId(1), []);
        long lsn1 = wal.Append(WalRecordType.Commit, new TransactionId(1), []);
        wal.FlushTo(lsn1);

        using var reader = wal.OpenReader(lsn1);
        reader.TryReadNext(out WalRecord rec).Should().BeTrue();
        rec.Lsn.Should().Be(lsn1);
        reader.TryReadNext(out _).Should().BeFalse();
    }

    // ------------------------------------------------------------------------
    // FT-27: WAL group commit batching
    // ------------------------------------------------------------------------

    [Fact]
    public void GroupCommit_WindowZero_PreservesLegacyBehavior()
    {
        // window=0 (既定) では従来の opportunistic group commit のみで、
        // 各 FlushTo は確実に fsync を起動する。シングルスレッド逐次 commit では
        // 1 commit = 1 batch になることを確認する。
        using var wal = new WriteAheadLog(_dir, 64L * 1024 * 1024, TimeSpan.Zero);
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
        using var wal = new WriteAheadLog(_dir, 64L * 1024 * 1024, TimeSpan.FromMilliseconds(5));

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
        using var wal = new WriteAheadLog(_dir, 64L * 1024 * 1024, TimeSpan.FromMilliseconds(2));
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
        using var wal = new WriteAheadLog(_dir, 64L * 1024 * 1024, TimeSpan.FromMilliseconds(3));
        long lastLsn = -1;
        for (int i = 0; i < 8; i++)
            lastLsn = wal.Append(WalRecordType.Commit, new TransactionId(i + 1), []);
        wal.FlushTo(lastLsn);
        wal.FlushedLsn.Should().BeGreaterThanOrEqualTo(lastLsn);
    }
}
