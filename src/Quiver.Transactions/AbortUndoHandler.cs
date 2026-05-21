using Quiver.Core;
using Quiver.Storage;
using Quiver.Wal;

namespace Quiver.Transactions;

/// <summary>
/// FT-15 Tier1: 進行中トランザクションが abort / コミット失敗したときに、キャプチャ済みの
/// before-image を所有データファイルへ書き戻し、ページバックされたストアメタを再ロードする。
///
/// バイナリバックエンドの ACID Atomicity を本物にするための「インプロセス undo」担当。
/// クラッシュ中の未コミットデータ漏れは <see cref="RecoveryManager"/> の undo パス
/// (CompensationLogRecord の再適用) が塞ぐ — 本クラスはその対になる即時版。
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
    /// 与えられた before-image (CLR ペイロード) を所有ファイルへ書き戻し、ストアメタを
    /// 再ロードする。最後に巻き戻したファイルを fsync し、abort 直後にクラッシュしても
    /// 未コミットデータがデータファイルへ残らないようにする (abort-then-crash 耐性)。
    /// </summary>
    public void Undo(IReadOnlyCollection<byte[]> beforeImagePayloads)
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
            }
        }

        if (touched.Count == 0) return;

        // ヘッダページも before-image で TX 開始前へ戻っているので、ストアの
        // インメモリメタ (hwm / freeHead / inUseCount) をページから読み直す。
        _reloadStoreMeta();

        // 巻き戻した状態を durable にしてから (Transaction 側が) Abort レコードを書く。
        // これにより「abort 後にクラッシュ」しても未コミットデータが残らない。
        foreach (var file in touched)
            file.Flush();
    }
}
