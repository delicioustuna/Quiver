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
}
