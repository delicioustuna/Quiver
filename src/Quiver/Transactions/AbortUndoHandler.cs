using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Wal;

namespace Quiver.Transactions;

/// <summary>
/// 進行中トランザクションが abort / コミット失敗したときに、キャプチャ済みの
/// before-image を所有データファイルへ書き戻し、ページバックされたストアメタを再ロードする。
/// バイナリバックエンドの ACID Atomicity を本物にするための「インプロセス undo」担当。
/// クラッシュ中の未コミットデータ漏れは <see cref="RecoveryManager"/> の undo パス
/// (CompensationLogRecord の再適用) が塞ぐ — 本クラスはその対になる即時版。
/// <see cref="UndoPartial"/> を追加し、savepoint への部分ロールバックでも
/// 同じ復元ロジックを再利用できるようにした。partial rollback は durable にしない
/// (commit 前のサブステップなので flush 不要) ことが abort との違い。
/// </summary>
internal sealed class AbortUndoHandler
{
    private readonly IReadOnlyDictionary<byte, IPagedFile> _files;
    private readonly Action _reloadStoreMeta;
    // leaf 論理 undo の適用器 (spec: 07_fulltext.md#logical-wal, tenant, isUpsert, key, value) → index manager。
    private readonly Action<byte, bool, byte[], long>? _applyFtUndo;

    /// <param name="files">fileKind → 所有 <see cref="IPagedFile"/> のレジストリ。</param>
    /// <param name="reloadStoreMeta">
    /// before-image 復元後にストアのインメモリメタ (hwm 等) を再同期するコールバック。
    /// </param>
    /// <param name="applyFtUndo">
    /// leaf 論理 undo の適用器。postings/norms の Suppressed leaf は page before-image を
    /// 持たないため、abort 時に逆操作 (Upsert↔Delete) で FT 索引から取り消す。null = FT 非対応 backend。
    /// </param>
    public AbortUndoHandler(
        IReadOnlyDictionary<byte, IPagedFile> files,
        Action reloadStoreMeta,
        Action<byte, bool, byte[], long>? applyFtUndo = null)
    {
        _files = files;
        _reloadStoreMeta = reloadStoreMeta;
        _applyFtUndo = applyFtUndo;
    }

    /// <summary>
    /// tx が発行した leaf 論理ミューテーションを **逆順 (LIFO)** に逆操作して
    /// FT 索引から取り消す。page before-image 復元 (<see cref="Undo"/>) と独立 (FT leaf は別ページ)。
    /// FT 索引のヘッダキャッシュ再同期は <see cref="Undo"/> 内の reloadStoreMeta が担う。
    /// </summary>
    public void UndoFtLogical(IReadOnlyList<Quiver.Storage.Wal.FtUndoEntry> ftUndoLog)
    {
        if (_applyFtUndo is null || ftUndoLog.Count == 0) return;
        for (int i = ftUndoLog.Count - 1; i >= 0; i--)
        {
            var e = ftUndoLog[i];
            _applyFtUndo(e.Tenant, e.IsUpsert, e.Key, e.Value);
        }
    }

    /// <summary>
    /// 監査 #2: <see cref="ITransaction.RollbackTo"/> の partial rollback 用 FT 論理 undo。
    /// <see cref="UndoFtLogical"/> (full abort) と異なり、各逆操作を **補償 FtLeafMutation** として
    /// WAL にも追記する。FtLeafMutation は eager 追記なので破棄分は commit した tx の WAL に残る。
    /// 補償を追記することで recovery Pass 2b が forward→補償で正しい (ロールバック後の) 状態へ収束する
    /// (補償が無いと crash 後に破棄分が Pass 2b で蘇る)。逆順 (LIFO) に適用する。
    /// </summary>
    public void UndoFtLogicalPartial(IReadOnlyList<Quiver.Storage.Wal.FtUndoEntry> ftUndoLog)
    {
        if (_applyFtUndo is null || ftUndoLog.Count == 0) return;
        for (int i = ftUndoLog.Count - 1; i >= 0; i--)
        {
            var e = ftUndoLog[i];
            // WAL 補償: forward Upsert → 補償 Delete / forward Delete → 補償 Upsert(旧値)。
            var inverseOp = e.IsUpsert
                ? Quiver.Storage.Wal.FtLeafMutationCodec.Op.Delete
                : Quiver.Storage.Wal.FtLeafMutationCodec.Op.Upsert;
            WalPageContext.LogFtLeafCompensation(inverseOp, e.Tenant, e.Key, e.Value);
            // live tree: 逆操作を適用 (undo Upsert=delete / undo Delete=旧値で再挿入)。
            _applyFtUndo(e.Tenant, e.IsUpsert, e.Key, e.Value);
        }
    }

    /// <summary>
    /// 与えられた before-image (CLR ペイロード) を所有ファイルへ書き戻し、ストアメタを
    /// 再ロードする。最後に巻き戻したファイルを fsync し、abort 直後にクラッシュしても
    /// 未コミットデータがデータファイルへ残らないようにする (abort-then-crash 耐性)。
    /// </summary>
    public void Undo(IReadOnlyCollection<byte[]> beforeImagePayloads)
        => UndoCore(beforeImagePayloads, flushAfter: true, updatePending: false);

    /// <summary>
    /// <see cref="ITransaction.RollbackTo"/> の partial rollback 用。
    /// before-image をデータファイル + バッファプールへ復元し、ストアメタを再ロードする。
    /// abort と異なり <c>flushAfter: false</c> でフラッシュしない (commit 前のサブステップ
    /// であり durable 化は最終 commit に委ねる)。また、各復元ページの内容を
    /// <c>_pending</c> へ反映し、後続 commit 時の WAL PageImage が rollback 後の状態を
    /// 正しく永続化するようにする (= savepoint なしの flat tx に対する PageImage 整合性を維持)。
    /// </summary>
    public void UndoPartial(IReadOnlyCollection<byte[]> beforeImagePayloads)
        => UndoCore(beforeImagePayloads, flushAfter: false, updatePending: true);

    private void UndoCore(
        IReadOnlyCollection<byte[]> beforeImagePayloads,
        bool flushAfter,
        bool updatePending)
    {
        if (beforeImagePayloads.Count == 0) return;

        var touched = new HashSet<IPagedFile>();
        foreach (var payload in beforeImagePayloads)
        {
            if (!WalPageImageCodec.TryDecode(payload, out byte fileKind, out long pageId, out var pageBytes))
                continue;
            if (_files.TryGetValue(fileKind, out var file))
            {
                // WritePageForRecovery はバッファプールをバイパスしてデータファイルへ
                // 直接書き、キャッシュ済みフレームも before-image で上書きして dirty を落とす。
                file.WritePageForRecovery(new PageId(pageId), pageBytes);
                touched.Add(file);

                // partial rollback の場合、後続 commit でこのページの PageImage が
                // 「rollback 後の内容」になるよう _pending を上書きする。abort 経路では
                // _pending ごと丸ごと破棄されるので呼ばない。
                if (updatePending)
                    WalPageContext.OverwritePendingFromBeforeImage(fileKind, pageId, pageBytes);
            }
        }

        if (touched.Count == 0) return;

        // ヘッダページも before-image で TX 開始前へ戻っているので、ストアの
        // インメモリメタ (hwm / freeHead / inUseCount) をページから読み直す。
        _reloadStoreMeta();

        if (!flushAfter) return;
        // 巻き戻した状態を durable にしてから (Transaction 側が) Abort レコードを書く。
        // これにより「abort 後にクラッシュ」しても未コミットデータが残らない。
        foreach (var file in touched)
            file.Flush();
    }
}
