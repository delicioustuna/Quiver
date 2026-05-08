using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;
using GraphDb.Engine.Wal;

namespace GraphDb.Engine.Transactions;

internal sealed class RecoveryManager : IRecoveryManager
{
    private readonly IPageManager _pageManager;
    private readonly IWriteAheadLog _wal;

    public RecoveryManager(IPageManager pageManager, IWriteAheadLog wal)
    {
        _pageManager = pageManager;
        _wal = wal;
    }

    public long Recover()
    {
        long checkpointLsn = FindLastCheckpointLsn();
        long lastLsn = -1;
        var activeTxs = new HashSet<long>();
        var committedTxs = new HashSet<long>();

        using var reader = _wal.OpenReader(checkpointLsn);
        while (reader.TryReadNext(out var record))
        {
            lastLsn = record.Lsn;
            switch (record.Type)
            {
                case WalRecordType.Begin:
                    activeTxs.Add(record.TransactionId.Value);
                    break;
                case WalRecordType.Commit:
                    activeTxs.Remove(record.TransactionId.Value);
                    committedTxs.Add(record.TransactionId.Value);
                    break;
                case WalRecordType.Abort:
                    activeTxs.Remove(record.TransactionId.Value);
                    break;
                case WalRecordType.PageImage:
                    // Phase 2: apply page image to the appropriate file.
                    // Requires a file registry keyed by file-kind that maps to IPagedFile.
                    // For now: only replay images whose transaction has committed.
                    if (committedTxs.Contains(record.TransactionId.Value))
                        ApplyPageImage(record);
                    break;
                case WalRecordType.Checkpoint:
                    // Next Recover pass can start from the newest checkpoint.
                    break;
            }
        }

        // activeTxs: incomplete at crash time — their dirty pages are simply not replayed.
        return lastLsn;
    }

    // Scan the WAL once to find the LSN of the last Checkpoint record.
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

    // Phase 2 hook: write a committed page image back to the owning IPagedFile.
    private static void ApplyPageImage(in WalRecord record)
    {
        // TODO Phase 2: decode (fileKind, pageId, pageBytes) from record.Payload
        // and call IPagedFile.PinForWrite / write / UnpinDirty.
    }
}
