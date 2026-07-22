using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Transactions.Tests;

public sealed class WalWinnerLoserTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "quiver_wal_winner_loser_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Recovery_replays_only_page_images_owned_by_a_valid_commit()
    {
        Directory.CreateDirectory(_directory);
        string walPath = Path.Combine(_directory, "wal");
        string sourcePath = Path.Combine(_directory, "source.db");
        string destinationPath = Path.Combine(_directory, "destination.db");

        using var wal = new WriteAheadLog(walPath);
        PageId winnerPage;
        PageId loserPage;
        using (IPagedFile source = new PagedFile(sourcePath))
        {
            source.EnableWalLogging(1, wal);
            winnerPage = WritePageImage(
                source,
                wal,
                new TransactionId(1),
                "WINNER",
                commit: true);
            loserPage = WritePageImage(
                source,
                wal,
                new TransactionId(2),
                "LOSER!",
                commit: false);
        }

        using IPagedFile destination = new PagedFile(destinationPath);
        var recovery = new RecoveryManager(
            new NullPageManager(),
            wal,
            new Dictionary<byte, IPagedFile> { { 1, destination } });
        recovery.Recover();

        var winner = destination.PinForRead(winnerPage);
        try
        {
            System.Text.Encoding.UTF8.GetString(winner.Data[..6])
                .Should().Be("WINNER");
        }
        finally
        {
            destination.Unpin(winnerPage);
        }
        destination.PageCount.Should().Be(
            loserPage.Value,
            "a loser PageImage must not allocate its destination page");
    }

    [Fact]
    public void Open_rejects_a_commit_with_an_invalid_checksum()
    {
        Directory.CreateDirectory(_directory);
        string walPath = Path.Combine(_directory, "wal-torn");
        string sourcePath = Path.Combine(_directory, "source-torn.db");

        using (var wal = new WriteAheadLog(walPath))
        using (IPagedFile source = new PagedFile(sourcePath))
        {
            source.EnableWalLogging(1, wal);
            WritePageImage(
                source,
                wal,
                new TransactionId(7),
                "TORN!!",
                commit: true);
        }

        using (var stream = new FileStream(
                   walPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            stream.Position = stream.Length - 1;
            int value = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(value ^ 0x80));
            stream.Flush(flushToDisk: true);
        }

        Action reopen = () => new WriteAheadLog(walPath);
        reopen.Should().Throw<CorruptionException>(
            "a checksum-invalid Commit must never make the transaction a winner");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static PageId WritePageImage(
        IPagedFile source,
        WriteAheadLog wal,
        TransactionId transactionId,
        string marker,
        bool commit)
    {
        wal.Append(
            WalRecordType.BeginWrite,
            transactionId,
            ReadOnlySpan<byte>.Empty);
        var writeSet = new WalWriteSet(wal, transactionId);
        wal.ActiveWriteSet = writeSet;
        PageId pageId = source.AllocatePage(PageKind.VertexRecord);
        var page = source.PinForWrite(pageId);
        try
        {
            System.Text.Encoding.UTF8.GetBytes(marker).CopyTo(page.Data);
        }
        finally
        {
            page.Dispose();
        }
        writeSet.FlushPending();
        long finalLsn = commit
            ? wal.Append(
                WalRecordType.Commit,
                transactionId,
                ReadOnlySpan<byte>.Empty)
            : wal.CurrentLsn;
        wal.FlushTo(finalLsn);
        wal.ActiveWriteSet = null;
        return pageId;
    }

    private sealed class NullPageManager : IPageManager
    {
        public IPagedFile OpenOrCreate(string path, PageKind defaultKind)
            => throw new NotSupportedException();

        public void FlushAll() { }

        public void Dispose() { }
    }
}
