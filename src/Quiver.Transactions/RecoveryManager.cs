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

        // Pass 2 (redo): コミット済みトランザクションを順方向で再生する。
        //   - PageImage (after-image): データページに上書き
        //   - FT-18: IndexMutation: B+Tree 索引へ <strong>順方向</strong>に冪等再生する。
        //     索引ファイルは WAL ページロギング対象外なので redo は論理。
        //     <see cref="IIndexManager.ApplyEncodedIndexMutation"/> は (key, value) 存在検査付き
        //     冪等経路なので、checkpoint 後に OS-MMF steal で部分的に索引ファイルへ漏れた
        //     ページがあっても二重適用にならない。
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                if (!committedTxs.Contains(record.TransactionId.Value)) continue;
                if (record.Type == WalRecordType.PageImage)
                    ApplyPagePayload(record);
                else if (record.Type == WalRecordType.IndexMutation)
                    ApplyIndexForward(record);
            }
        }

        // Pass 3 (undo):
        //   - FT-15 Tier2 ARIES undo (CompensationLogRecord — 物理 before-image): 真にクラッシュで
        //     宙ぶらりんになった未完了 TX のみ。Commit / Abort / PageImage いずれも持たない TX。
        //     PageImage を持つ TX は FlushPending を通過済み = コミットパスで Commit レコードが
        //     torn しただけなので undo してはいけない (committed データを破壊する)。
        //   - FT-17 + FT-18 索引論理 undo (IndexMutation): Commit を持たない全 TX を対象にする。
        //     索引の in-process abort はバッファプール経由で durable でないので、aborted TX も
        //     再 undo する必要がある (冪等経路なのでインプロセス inverse の二重適用は安全)。
        //     PageImage の有無は索引と無関係なので index undo の判定には使わない。
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                long txId = record.TransactionId.Value;
                if (committedTxs.Contains(txId)) continue;
                if (record.Type == WalRecordType.CompensationLogRecord)
                {
                    if (abortedTxs.Contains(txId) || txsWithPageImage.Contains(txId)) continue;
                    ApplyPagePayload(record);
                }
                else if (record.Type == WalRecordType.IndexMutation)
                {
                    // committed のみ除外。aborted / 真の未完了の両方を冪等に逆適用する。
                    ApplyIndexUndo(record);
                }
            }
        }

        return lastLsn;
    }

    /// <summary>
    /// FT-18: IndexMutation WAL レコードをデコードし、その操作をそのまま (順方向に)
    /// 索引へ冪等適用する。コミット済み TX の crash redo に使う。
    /// </summary>
    private void ApplyIndexForward(in WalRecord record)
    {
        if (_indexManager == null) return;
        if (!IndexMutationCodec.TryDecode(
                record.Payload.Span,
                out string indexName, out IndexKeyKind keyKind,
                out long value, out bool isInsert, out byte[] keyBytes))
            return;
        _indexManager.ApplyEncodedIndexMutation(
            indexName, keyKind, keyBytes, value, isInsert);
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
    /// FT-17 / FT-18: IndexMutation WAL レコードをデコードし、その<strong>逆</strong>操作
    /// (Insert↔Delete) を冪等に索引へ適用する。Commit レコードを持たない TX (真の未完了
    /// + aborted の両方) の B+Tree インデックス変更を巻き戻す。<see cref="ApplyEncodedIndexMutation"/>
    /// は <c>(key, value)</c> 存在検査付きなので、(a) forward op が steal で索引ファイルに
    /// 漏れていなかった場合、(b) in-process abort で既に逆適用済みの場合のいずれも安全に no-op。
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
