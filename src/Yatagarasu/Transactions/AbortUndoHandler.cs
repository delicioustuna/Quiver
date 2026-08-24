using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Wal;

namespace Yatagarasu.Transactions;

/// <summary>
/// 進行中トランザクションが abort / コミット失敗したときに、キャプチャ済みの
/// before-image を所有データファイルへ書き戻し、ページバックされたストアメタを再ロードする。
/// バイナリバックエンドの ACID Atomicity を本物にするための「インプロセス undo」担当。
/// <see cref="UndoPartial"/> を追加し、savepoint への部分ロールバックでも
/// 同じ復元ロジックを再利用できるようにした。partial rollback は durable にしない
/// (commit 前のサブステップなので flush 不要) ことが abort との違い。
/// </summary>
internal sealed class AbortUndoHandler
{
    private readonly IReadOnlyDictionary<byte, IPagedFile> _files;
    private readonly Action _reloadStoreMeta;
    /// <param name="files">fileKind → 所有 <see cref="IPagedFile"/> のレジストリ。</param>
    /// <param name="reloadStoreMeta">
    /// before-image 復元後にストアのインメモリメタ (hwm 等) を再同期するコールバック。
    /// </param>
    public AbortUndoHandler(
        IReadOnlyDictionary<byte, IPagedFile> files,
        Action reloadStoreMeta)
    {
        _files = files;
        _reloadStoreMeta = reloadStoreMeta;
    }

    /// <summary>
    /// 与えられた before-image payload を所有ファイルへ書き戻し、ストアメタを
    /// 再ロードする。最後に巻き戻したファイルを fsync し、abort 直後にクラッシュしても
    /// 未コミットデータがデータファイルへ残らないようにする (abort-then-crash 耐性)。
    /// </summary>
    public void Undo(IReadOnlyCollection<byte[]> beforeImagePayloads)
        => UndoCore(beforeImagePayloads, flushAfter: true);

    /// <summary>
    /// <see cref="ITransaction.RollbackTo"/> の partial rollback 用。
    /// before-image をデータファイル + バッファプールへ復元し、ストアメタを再ロードする。
    /// abort と異なり <c>flushAfter: false</c> でフラッシュしない (commit 前のサブステップ
    /// であり durable 化は最終 commit に委ねる)。また、各復元ページの内容を
    /// <c>_pending</c> へ反映し、後続 commit 時の WAL PageImage が rollback 後の状態を
    /// 正しく永続化するようにする (= savepoint なしの flat tx に対する PageImage 整合性を維持)。
    /// </summary>
    public void UndoPartial(
        IReadOnlyCollection<byte[]> beforeImagePayloads,
        WalWriteSet writeSet)
        => UndoCore(beforeImagePayloads, flushAfter: false, writeSet);

    private void UndoCore(
        IReadOnlyCollection<byte[]> beforeImagePayloads,
        bool flushAfter,
        WalWriteSet? writeSet = null)
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
                writeSet?.OverwritePendingFromBeforeImage(fileKind, pageId, pageBytes);
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
