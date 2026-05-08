using GraphDb.Engine.Core;

namespace GraphDb.Engine.Wal;

/// <summary>WAL ライタ・リーダの統合インタフェース。</summary>
public interface IWriteAheadLog : IDisposable
{
    long CurrentLsn { get; }
    long FlushedLsn { get; }

    long Append(WalRecordType type, TransactionId tx, ReadOnlySpan<byte> payload);
    void FlushTo(long lsn);
    long WriteCheckpoint(long oldestActiveLsn, long lastFlushedDataLsn);
    void Truncate(long uptoLsn);
    IWalReader OpenReader(long startLsn);
}
