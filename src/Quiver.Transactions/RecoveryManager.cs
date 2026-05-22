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
    // FT-17: null でない場合、未コミット TX の IndexMutation レコードを逆適用して
    // B+Tree インデックスのエントリを巻き戻す。
    private readonly IIndexManager? _indexManager;

    public RecoveryManager(IPageManager pageManager, IWriteAheadLog wal)
        : this(pageManager, wal, []) { }

    public RecoveryManager(
        IPageManager pageManager,
        IWriteAheadLog wal,
        Dictionary<byte, IPagedFile> fileRegistry,
        IIndexManager? indexManager = null)
    {
        _pageManager = pageManager;
        _wal = wal;
        _fileRegistry = fileRegistry;
        _indexManager = indexManager;
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
            }
        }

        // Pass 2 (redo): コミット済みトランザクションの PageImage (after-image) を replay する。
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

        // Pass 3 (undo) — FT-15 Tier2 ARIES undo + FT-17 索引論理 undo:
        // クラッシュで宙ぶらりんになった真の未完了トランザクションの
        // CompensationLogRecord (ページ before-image) と IndexMutation (B+Tree 索引の
        // 論理ミューテーション) を再適用 / 逆適用し、未コミットの変更を巻き戻す。
        // undo 対象は以下をすべて満たすトランザクションのみ:
        //   - Commit レコードを持たない (コミット済みは redo 済み)
        //   - Abort レコードを持たない (インプロセス abort 済みは巻き戻しが durable)
        //   - PageImage を 1 件も持たない (PageImage があるなら FlushPending を通過した
        //     = コミットパスに入っており、Commit レコードが torn しただけ。これを undo
        //     するとデータファイルへ flush 済みのコミット内容まで消してしまう)
        // シングルライタ前提では、クラッシュした未完了トランザクションは WAL 末尾に 1 つだけ
        // 存在し、その before-image は当該ページの最後のコミット内容と一致するため、
        // redo → undo の順序に依らず正しい状態へ収束する。
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                long txId = record.TransactionId.Value;
                if (committedTxs.Contains(txId) ||
                    abortedTxs.Contains(txId) ||
                    txsWithPageImage.Contains(txId))
                    continue;
                if (record.Type == WalRecordType.CompensationLogRecord)
                    ApplyPagePayload(record);
                else if (record.Type == WalRecordType.IndexMutation)
                    ApplyIndexUndo(record);
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

    /// <summary>
    /// FT-17: IndexMutation WAL レコードをデコードし、その逆操作 (Insert↔Delete) を
    /// 索引へ適用して、未コミット TX の B+Tree インデックス変更を巻き戻す。
    /// </summary>
    private void ApplyIndexUndo(in WalRecord record)
    {
        if (_indexManager == null) return;
        if (!IndexMutationCodec.TryDecode(
                record.Payload.Span,
                out string indexName, out IndexKeyKind keyKind,
                out long value, out bool isInsert, out byte[] keyBytes))
            return;
        // 記録された操作の逆を適用する: Insert は Delete、Delete は Insert。
        _indexManager.ApplyEncodedIndexMutation(
            indexName, keyKind, keyBytes, value, isInsert: !isInsert);
    }
}
