using FluentAssertions;
using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;
using GraphDb.Engine.Wal;
using Xunit;

namespace GraphDb.Engine.Transactions.Tests;

public class RecoveryManagerTests : IDisposable
{
    private readonly string _walDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly WriteAheadLog _wal;
    private readonly RecoveryManager _recovery;

    public RecoveryManagerTests()
    {
        _wal = new WriteAheadLog(_walDir);
        _recovery = new RecoveryManager(new NullPageManager(), _wal);
    }

    public void Dispose()
    {
        _wal.Dispose();
        if (Directory.Exists(_walDir)) Directory.Delete(_walDir, recursive: true);
    }

    [Fact]
    public void Recover_on_empty_wal_returns_minus_one()
    {
        long lsn = _recovery.Recover();
        lsn.Should().Be(-1);
    }

    [Fact]
    public void Recover_returns_last_lsn_after_begin_and_commit()
    {
        var txId = new TransactionId(1);
        _wal.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
        long commitLsn = _wal.Append(WalRecordType.Commit, txId, ReadOnlySpan<byte>.Empty);
        _wal.FlushTo(commitLsn);

        long recovered = _recovery.Recover();
        recovered.Should().Be(commitLsn);
    }

    [Fact]
    public void Recover_returns_last_lsn_after_aborted_transaction()
    {
        var txId = new TransactionId(2);
        _wal.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
        long abortLsn = _wal.Append(WalRecordType.Abort, txId, ReadOnlySpan<byte>.Empty);
        _wal.FlushTo(abortLsn);

        long recovered = _recovery.Recover();
        recovered.Should().Be(abortLsn);
    }

    [Fact]
    public void Recover_processes_multiple_committed_transactions()
    {
        for (int i = 0; i < 5; i++)
        {
            var txId = new TransactionId(i);
            _wal.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
            _wal.Append(WalRecordType.Commit, txId, ReadOnlySpan<byte>.Empty);
        }
        long lastLsn = _wal.CurrentLsn;
        _wal.FlushTo(lastLsn);

        long recovered = _recovery.Recover();
        recovered.Should().Be(lastLsn);
    }

    [Fact]
    public void Recover_handles_mixed_committed_and_aborted_transactions()
    {
        var committed = new TransactionId(10);
        var aborted = new TransactionId(11);

        _wal.Append(WalRecordType.Begin, committed, ReadOnlySpan<byte>.Empty);
        _wal.Append(WalRecordType.Begin, aborted, ReadOnlySpan<byte>.Empty);
        _wal.Append(WalRecordType.Commit, committed, ReadOnlySpan<byte>.Empty);
        long lastLsn = _wal.Append(WalRecordType.Abort, aborted, ReadOnlySpan<byte>.Empty);
        _wal.FlushTo(lastLsn);

        long recovered = _recovery.Recover();
        recovered.Should().Be(lastLsn);
    }

    private sealed class NullPageManager : IPageManager
    {
        public IPagedFile OpenOrCreate(string path, PageKind defaultKind) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
