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
    long WriteCheckpoint(long oldestActiveLsn, long lastFlushedDataLsn);
    void Truncate(long uptoLsn);
    IWalReader OpenReader(long startLsn);
}
