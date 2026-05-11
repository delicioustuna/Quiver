using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Wal;

namespace Quiver.Transactions;

internal sealed class RecoveryManager : IRecoveryManager
{
    private readonly IPageManager _pageManager;
    private readonly IWriteAheadLog _wal;
    private readonly Dictionary<byte, IPagedFile> _fileRegistry;

    public RecoveryManager(IPageManager pageManager, IWriteAheadLog wal)
        : this(pageManager, wal, []) { }

    public RecoveryManager(
        IPageManager pageManager,
        IWriteAheadLog wal,
        Dictionary<byte, IPagedFile> fileRegistry)
    {
        _pageManager = pageManager;
        _wal = wal;
        _fileRegistry = fileRegistry;
    }

    public long Recover()
    {
        long checkpointLsn = FindLastCheckpointLsn();

        // Pass 1: determine which transactions committed and find the last LSN.
        var committedTxs = new HashSet<long>();
        long lastLsn = -1;
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                lastLsn = record.Lsn;
                if (record.Type == WalRecordType.Commit)
                    committedTxs.Add(record.TransactionId.Value);
            }
        }

        // Pass 2: replay PageImage records for committed transactions only.
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                if (record.Type == WalRecordType.PageImage &&
                    committedTxs.Contains(record.TransactionId.Value))
                {
                    ApplyPageImage(record);
                }
            }
        }

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

    /// <summary>
    /// Decode a PageImage WAL record and write the page bytes directly to the owning file.
    /// Payload format (version 1): [version:1][fileKind:1][pageId:8][pageBytes:N]
    /// </summary>
    private void ApplyPageImage(in WalRecord record)
    {
        var payload = record.Payload.Span;
        if (payload.Length < 10) return;
        if (payload[0] != 1) return; // unsupported version
        byte fileKind = payload[1];
        long pageId = BinaryPrimitives.ReadInt64LittleEndian(payload[2..]);
        var pageBytes = payload[10..];

        if (!_fileRegistry.TryGetValue(fileKind, out var file)) return;
        file.WritePageForRecovery(new PageId(pageId), pageBytes);
    }
}
