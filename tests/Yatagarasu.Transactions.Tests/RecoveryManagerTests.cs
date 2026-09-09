using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Wal;
using Xunit;

namespace Yatagarasu.Transactions.Tests;

public class RecoveryManagerTests : IDisposable
{
    private readonly string _walDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly WriteAheadLog _wal;
    private readonly RecoveryManager _recovery;

    public RecoveryManagerTests()
    {
        _wal = new WriteAheadLog(Path.Combine(_walDir, "wal"));
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
        _wal.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
        long commitLsn = _wal.Append(WalRecordType.Commit, txId, ReadOnlySpan<byte>.Empty);
        _wal.FlushTo(commitLsn);

        long recovered = _recovery.Recover();
        recovered.Should().Be(commitLsn);
    }

    [Fact]
    public void Recover_returns_last_lsn_after_aborted_transaction()
    {
        var txId = new TransactionId(2);
        _wal.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
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
            _wal.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
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

        _wal.Append(WalRecordType.BeginWrite, committed, ReadOnlySpan<byte>.Empty);
        _wal.Append(WalRecordType.BeginWrite, aborted, ReadOnlySpan<byte>.Empty);
        _wal.Append(WalRecordType.Commit, committed, ReadOnlySpan<byte>.Empty);
        long lastLsn = _wal.Append(WalRecordType.Abort, aborted, ReadOnlySpan<byte>.Empty);
        _wal.FlushTo(lastLsn);

        long recovered = _recovery.Recover();
        recovered.Should().Be(lastLsn);
    }

    // ===== PageImage replay =====

    [Fact]
    public void ApplyPageImage_writes_committed_page_bytes_to_file()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dataDir);
        string srcPath = Path.Combine(dataDir, "src.db");
        string dstPath = Path.Combine(dataDir, "dst.db");
        try
        {
            // フェーズ 1: WAL logging 経路でページを書き、有効な PageImage レコードを生成する。
            PageId dataPage;
            {
                IPagedFile srcFile = new PagedFile(srcPath);
                srcFile.EnableWalLogging(1, _wal);
                var txId = new TransactionId(42);
                _wal.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
                var writeSet = new WalWriteSet(_wal, txId);
                _wal.ActiveWriteSet = writeSet;
                dataPage = srcFile.AllocatePage(PageKind.VertexRecord);
                var ph = srcFile.PinForWrite(dataPage);
                System.Text.Encoding.UTF8.GetBytes("RECOVERED").CopyTo(ph.Data);
                ph.Dispose(); // UnpinDirty → PageImage をトランザクションバッファにコアレス
                writeSet.FlushPending(); // コミット直前にバッファをWALへ追記
                long commitLsn = _wal.Append(WalRecordType.Commit, txId, ReadOnlySpan<byte>.Empty);
                _wal.FlushTo(commitLsn);
                _wal.ActiveWriteSet = null;
                srcFile.Dispose();
            }

            // フェーズ 2: 新しい宛先ファイルへ復旧する。
            using IPagedFile dstFile = new PagedFile(dstPath);
            var registry = new Dictionary<byte, IPagedFile> { { 1, dstFile } };
            var recovery = new RecoveryManager(new NullPageManager(), _wal, registry);
            recovery.Recover();

            // フェーズ 3: 復旧したページ内の sentinel を確認する。
            var rh = dstFile.PinForRead(dataPage);
            byte[] body = rh.Data[..9].ToArray();
            rh.Dispose();

            System.Text.Encoding.UTF8.GetString(body).Should().Be("RECOVERED");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyPageImage_skips_page_of_aborted_transaction()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dataDir);
        string srcPath = Path.Combine(dataDir, "src.db");
        string dstPath = Path.Combine(dataDir, "dst.db");
        try
        {
            // フェーズ 1: ページを書き込むがトランザクションを ABORT する。
            PageId dataPage;
            {
                IPagedFile srcFile = new PagedFile(srcPath);
                srcFile.EnableWalLogging(1, _wal);
                var txId = new TransactionId(99);
                _wal.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
                var writeSet = new WalWriteSet(_wal, txId);
                _wal.ActiveWriteSet = writeSet;
                dataPage = srcFile.AllocatePage(PageKind.VertexRecord);
                var ph = srcFile.PinForWrite(dataPage);
                System.Text.Encoding.UTF8.GetBytes("ABORTED!").CopyTo(ph.Data);
                ph.Dispose(); // UnpinDirty → PageImage をトランザクションバッファにコアレス
                // PageImage を WAL へ追記したうえで、Commit ではなく Abort で終える。
                // recovery は「WAL に PageImage はあるが Commit が無い」場合に skip するはず。
                writeSet.FlushPending();
                long abortLsn = _wal.Append(WalRecordType.Abort, txId, ReadOnlySpan<byte>.Empty);
                _wal.FlushTo(abortLsn);
                _wal.ActiveWriteSet = null;
                srcFile.Dispose();
            }

            // フェーズ 2: 新しい宛先ファイルへ復旧する。
            using IPagedFile dstFile = new PagedFile(dstPath);
            var registry = new Dictionary<byte, IPagedFile> { { 1, dstFile } };
            var recovery = new RecoveryManager(new NullPageManager(), _wal, registry);
            recovery.Recover();

            // フェーズ 3: abort されたページが replay されないことを確認する。
            // dst file には page 0 (meta) だけがあり、dataPage (page 1) は適用されない。
            dstFile.PageCount.Should().Be(1, "aborted PageImage must not be written to the recovery target");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyPageImage_does_not_replace_a_page_with_a_newer_lsn()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dataDir);
        string sourcePath = Path.Combine(dataDir, "source.db");
        string destinationPath = Path.Combine(dataDir, "destination.db");
        try
        {
            PageId pageId;
            var txId = new TransactionId(77);
            using (IPagedFile source = new PagedFile(sourcePath))
            {
                source.EnableWalLogging(1, _wal);
                pageId = source.AllocatePage(PageKind.VertexRecord);
                _wal.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
                var writeSet = new WalWriteSet(_wal, txId);
                _wal.ActiveWriteSet = writeSet;
                using (var page = source.PinForWrite(pageId))
                    System.Text.Encoding.UTF8.GetBytes("OLDER").CopyTo(page.Data);
                writeSet.FlushPending();
                long commitLsn = _wal.Append(
                    WalRecordType.Commit,
                    txId,
                    ReadOnlySpan<byte>.Empty);
                _wal.FlushTo(commitLsn);
                _wal.ActiveWriteSet = null;
            }

            using IPagedFile destination = new PagedFile(destinationPath);
            PageId destinationPage = destination.AllocatePage(PageKind.VertexRecord);
            destinationPage.Should().Be(pageId);
            var destinationWrite = destination.PinForWrite(destinationPage);
            try
            {
                System.Text.Encoding.UTF8.GetBytes("NEWER").CopyTo(destinationWrite.Data);
                destinationWrite.Lsn = 10_000;
            }
            finally
            {
                destinationWrite.Dispose();
            }
            destination.Flush();

            var registry = new Dictionary<byte, IPagedFile> { { 1, destination } };
            new RecoveryManager(new NullPageManager(), _wal, registry).Recover();

            using var recovered = destination.PinForRead(destinationPage);
            System.Text.Encoding.UTF8.GetString(recovered.Data[..5])
                .Should().Be("NEWER");
            PageHeader.ReadLsn(recovered.Raw).Should().Be(10_000);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private sealed class NullPageManager : IPageManager
    {
        public IPagedFile OpenOrCreate(string path, PageKind defaultKind) => throw new NotSupportedException();
        public void FlushAll() { }
        public void Dispose() { }
    }
}
