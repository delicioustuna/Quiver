using Quiver.Core;

namespace Quiver.Wal;

/// <summary>WAL ライタ・リーダの統合インタフェース。</summary>
public interface IWriteAheadLog : IDisposable
{
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
    /// 旧 1 段チェックポイントレコード (FT-21 以前)。新規パスは
    /// <see cref="WriteCheckpointBegin"/> / <see cref="WriteCheckpointEnd"/> を使う。
    /// 既存 DB との互換のため残す。
    /// </summary>
    long WriteCheckpoint(long oldestActiveLsn, long lastFlushedDataLsn);

    /// <summary>
    /// FT-21: チェックポイント開始 sentinel。dirty page flush の前に書いて fsync する。
    /// 戻り値はこのレコードの LSN で、ペアとなる <see cref="WriteCheckpointEnd"/> に渡す。
    /// </summary>
    long WriteCheckpointBegin(long oldestActiveLsn, int dirtyPageCount);

    /// <summary>
    /// FT-21: チェックポイント完了 sentinel。全 page + index の fsync が完了してから書く。
    /// <paramref name="beginLsn"/> は対応する <see cref="WriteCheckpointBegin"/> の戻り値。
    /// recovery はこの End レコードを持つチェックポイントだけを「完了済み」と認識する。
    /// </summary>
    long WriteCheckpointEnd(long beginLsn);

    void Truncate(long uptoLsn);
    IWalReader OpenReader(long startLsn);
}
