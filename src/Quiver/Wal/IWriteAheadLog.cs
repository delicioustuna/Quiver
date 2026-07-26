using Quiver.Core;

namespace Quiver.Storage.Wal;

/// <summary>WAL ライタ・リーダの統合インタフェース。</summary>
internal interface IWriteAheadLog : IDisposable
{
    WalWriteSet? ActiveWriteSet
    {
        get => null;
        set { }
    }

    long CurrentLsn { get; }
    long FlushedLsn { get; }

    /// <summary>
    /// これまでに <see cref="Append"/> したレコードバイト数の累積 (単調増加)。
    /// <see cref="Truncate"/> で過去セグメントを削除しても減らない。
    /// チェックポイント契機の「前回チェックポイント以降の WAL 成長量」判定に使う。
    /// </summary>
    long BytesWritten { get; }

    long Append(WalRecordType type, TransactionId tx, ReadOnlySpan<byte> payload);
    void FlushTo(long lsn);

    /// <summary>
    /// ページヘッダへ割り当て LSN を刻んだ after-image を追記する。
    /// 戻り値は PageImage record とページヘッダで共有する LSN。
    /// </summary>
    long AppendPageImage(
        TransactionId tx,
        byte fileKind,
        long pageId,
        ReadOnlySpan<byte> pageBytes)
    {
        byte[] payload = WalPageImageCodec.Encode(fileKind, pageId, pageBytes);
        return Append(WalRecordType.PageImage, tx, payload);
    }

    /// <summary>
    /// チェックポイント開始 sentinel。dirty page flush の前に書いて fsync する。
    /// 戻り値はこのレコードの LSN で、ペアとなる <see cref="WriteCheckpointEnd"/> に渡す。
    /// </summary>
    long WriteCheckpointBegin(long oldestActiveLsn, int dirtyPageCount);

    /// <summary>
    /// チェックポイント完了 sentinel。全 page + index の fsync が完了してから書く。
    /// <paramref name="beginLsn"/> は対応する <see cref="WriteCheckpointBegin"/> の戻り値。
    /// recovery はこの End レコードを持つチェックポイントだけを「完了済み」と認識する。
    /// </summary>
    long WriteCheckpointEnd(long beginLsn);

    /// <summary>
    /// 指定 fileKind のページファイルを <paramref name="newPageCount"/> へ物理 truncate
    /// したことを WAL に記録する。書き込み直後に <see cref="FlushTo"/> で durable 化することで、
    /// 「WAL の FileTruncate より物理 truncate が先行した状態」を crash でも recovery 側が
    /// 再現できる (= 物理操作の冪等再生)。呼び出し側は本メソッドが返ってから
    /// <c>PagedFile.Truncate</c> を実行する。
    /// </summary>
    long WriteFileTruncate(byte fileKind, long newPageCount);

    void Truncate(long uptoLsn);
    IWalReader OpenReader(long startLsn);
}
