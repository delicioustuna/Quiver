using Quiver.Core;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Wal;

namespace Quiver.Transactions;

internal sealed class RecoveryManager : IRecoveryManager
{
    private readonly IPageManager _pageManager;
    private readonly IWriteAheadLog _wal;
    private readonly Dictionary<byte, IPagedFile> _fileRegistry;

    public RecoveryManager(IPageManager pageManager, IWriteAheadLog wal)
        : this(pageManager, wal, []) { }

    public RecoveryManager(
        IPageManager pageManager,
        IWriteAheadLog wal,
        Dictionary<byte, IPagedFile> fileRegistry,
        IIndexManager? indexManager = null)
    {
        // FT-19: indexManager 引数は呼び出し側互換のため受け取るが、索引が ARIES page-WAL
        // 対象になったので recovery 内では使わない。fileRegistry に索引も登録されている前提で、
        // PageImage / CLR の replay 経路が透過的に索引にも適用される。
        _ = indexManager;
        _pageManager = pageManager;
        _wal = wal;
        _fileRegistry = fileRegistry;
    }

    public long Recover()
    {
        long checkpointLsn = FindLastCheckpointLsn();

        // Pass 1: コミット済み / アボート済みトランザクションを分類し、最終 LSN を求める。
        // PageImage を持つトランザクションも記録する: PageImage (after-image) は
        // Transaction.Commit() のコミットパス (FlushPending) でのみ書かれるため、
        // 「PageImage はあるが Commit が無い」= コミット直前で Commit レコードが torn /
        // 欠落しただけ、と判別できる。クラッシュで宙ぶらりんになった真の未完了
        // トランザクションは FlushPending を通らないので PageImage を 1 件も持たない。
        var committedTxs = new HashSet<long>();
        var abortedTxs = new HashSet<long>();
        var txsWithPageImage = new HashSet<long>();
        long lastLsn = -1;
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                lastLsn = record.Lsn;
                if (record.Type == WalRecordType.Commit)
                    committedTxs.Add(record.TransactionId.Value);
                else if (record.Type == WalRecordType.Abort)
                    abortedTxs.Add(record.TransactionId.Value);
                else if (record.Type == WalRecordType.PageImage)
                    txsWithPageImage.Add(record.TransactionId.Value);
                // FT-19: WalRecordType.IndexMutation は予約値として残置されているが、
                // 新規 WAL には現れず、古い WAL に残っていても recovery では無視する
                // (索引は PageImage / CLR で覆われる)。
            }
        }

        // Pass 2 (redo): コミット済みトランザクションの PageImage (after-image) を replay する。
        // FT-19: 索引 PagedFile も EnableWalLogging により PageImage 経路を共有するため、
        // ここで fileRegistry に登録された索引ファイルにも自動的に redo が適用される。
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                if (record.Type == WalRecordType.PageImage &&
                    committedTxs.Contains(record.TransactionId.Value))
                {
                    ApplyPagePayload(record);
                }
            }
        }

        // Pass 3 (undo) — FT-15 Tier2 ARIES undo:
        // クラッシュで宙ぶらりんになった真の未完了トランザクションの CompensationLogRecord
        // (ページ before-image) を再適用し、未コミットの変更を巻き戻す。
        // undo 対象は以下をすべて満たすトランザクションのみ:
        //   - Commit レコードを持たない (コミット済みは redo 済み)
        //   - Abort レコードを持たない (インプロセス abort 済みは巻き戻しが durable)
        //   - PageImage を 1 件も持たない (PageImage があるなら FlushPending を通過した
        //     = コミットパスに入っており、Commit レコードが torn しただけ。これを undo
        //     するとデータファイルへ flush 済みのコミット内容まで消してしまう)
        // FT-19: 索引ファイルの CLR も同じ経路で扱われる (fileRegistry 経由)。
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                if (record.Type != WalRecordType.CompensationLogRecord) continue;
                long txId = record.TransactionId.Value;
                if (committedTxs.Contains(txId) ||
                    abortedTxs.Contains(txId) ||
                    txsWithPageImage.Contains(txId))
                    continue;
                ApplyPagePayload(record);
            }
        }

        return lastLsn;
    }

    // WAL を 1 回スキャンして、最後の Checkpoint レコードの LSN を求める。
    private long FindLastCheckpointLsn()
    {
        long checkpointLsn = 0;
        using var reader = _wal.OpenReader(0);
        while (reader.TryReadNext(out var record))
        {
            if (record.Type == WalRecordType.Checkpoint)
                checkpointLsn = record.Lsn;
        }
        return checkpointLsn;
    }

    /// <summary>
    /// PageImage (after-image, redo) または CompensationLogRecord (before-image, undo) の
    /// WAL レコードをデコードし、ページバイト列を所有ファイルへ直接書き込む。両者は
    /// <see cref="WalPageImageCodec"/> の同一フォーマットを共有する。
    /// </summary>
    private void ApplyPagePayload(in WalRecord record)
    {
        if (!WalPageImageCodec.TryDecode(
                record.Payload.Span, out byte fileKind, out long pageId, out var pageBytes))
            return;
        if (!_fileRegistry.TryGetValue(fileKind, out var file)) return;
        file.WritePageForRecovery(new PageId(pageId), pageBytes);
    }
}
