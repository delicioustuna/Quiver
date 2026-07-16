using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Storage.Wal;
using Quiver.Telemetry;

namespace Quiver.Transactions;

internal sealed class RecoveryManager : IRecoveryManager
{
    private readonly IWriteAheadLog _wal;
    private readonly Dictionary<byte, IPagedFile> _fileRegistry;
    private readonly CommittedTxRegistry? _committedRegistry;

    public RecoveryManager(IPageManager pageManager, IWriteAheadLog wal)
        : this(pageManager, wal, [])
    {
    }

    public RecoveryManager(
        IPageManager pageManager,
        IWriteAheadLog wal,
        Dictionary<byte, IPagedFile> fileRegistry,
        IIndexManager? indexManager = null,
        CommittedTxRegistry? committedRegistry = null)
    {
        _ = pageManager;
        _ = indexManager;
        _wal = wal;
        _fileRegistry = fileRegistry;
        _committedRegistry = committedRegistry;
    }

    public long Recover()
    {
        QuiverEventSource.Log.CrashRecovery();
        WalRecoveryScanResult scan = WalRecoveryScanner.Scan(_wal);

        if (_committedRegistry is not null)
        {
            foreach (long txId in scan.Winners)
                _committedRegistry.MarkCommitted(new TransactionId(txId));

            _committedRegistry.RecordMaxObservedTxId(scan.MaxObservedTxId);
            if (scan.MinObservedTxId is { } minTxId
                && minTxId > TransactionId.Bootstrap.Value)
            {
                _committedRegistry.RecoveryHorizon = minTxId - 1;
            }
        }

        using var reader = _wal.OpenReader(scan.RedoStartLsn);
        while (reader.TryReadNext(out WalRecord record))
        {
            if (record.Type == WalRecordType.PageImage
                && scan.Winners.Contains(record.TransactionId.Value))
            {
                ApplyPageImage(record);
            }
            else if (record.Type == WalRecordType.FileTruncate)
            {
                ApplyFileTruncate(record);
            }
        }

        return scan.LastLsn;
    }

    private void ApplyPageImage(in WalRecord record)
    {
        if (!WalPageImageCodec.TryDecode(
                record.Payload.Span,
                out byte fileKind,
                out long pageId,
                out byte[] pageBytes))
        {
            throw new CorruptionException($"Invalid PageImage payload at LSN {record.Lsn}.");
        }

        if (!_fileRegistry.TryGetValue(fileKind, out IPagedFile? file))
            throw new CorruptionException($"Unknown WAL file kind {fileKind} at LSN {record.Lsn}.");

        file.WritePageForRecovery(new PageId(pageId), pageBytes);
    }

    private void ApplyFileTruncate(in WalRecord record)
    {
        if (record.Payload.Length != 9)
            throw new CorruptionException($"Invalid FileTruncate payload at LSN {record.Lsn}.");

        ReadOnlySpan<byte> payload = record.Payload.Span;
        byte fileKind = payload[0];
        long newPageCount = BinaryPrimitives.ReadInt64LittleEndian(payload[1..]);
        if (newPageCount < 1)
            throw new CorruptionException($"Invalid FileTruncate page count at LSN {record.Lsn}.");
        if (!_fileRegistry.TryGetValue(fileKind, out IPagedFile? file))
            throw new CorruptionException($"Unknown WAL file kind {fileKind} at LSN {record.Lsn}.");

        file.Truncate(newPageCount);
    }
}

internal static class WalRecoveryScanner
{
    internal static WalRecoveryScanResult Scan(IWriteAheadLog wal)
    {
        var winners = new HashSet<long>();
        var checkpointBegins = new HashSet<long>();
        long redoStartLsn = 0;
        long lastLsn = -1;
        long maxObservedTxId = TransactionId.Bootstrap.Value;
        long? minObservedTxId = null;

        using var reader = wal.OpenReader(0);
        while (reader.TryReadNext(out WalRecord record))
        {
            lastLsn = record.Lsn;
            long txId = record.TransactionId.Value;
            if (txId > 0)
            {
                maxObservedTxId = Math.Max(maxObservedTxId, txId);
                minObservedTxId = minObservedTxId is { } current
                    ? Math.Min(current, txId)
                    : txId;
            }

            switch (record.Type)
            {
                case WalRecordType.Commit:
                    // PageImage の存在から commit を推定すると、torn Commit を winner と誤認する。
                    // durable winner は checksum が有効な明示 Commit だけで決める。
                    winners.Add(txId);
                    break;

                case WalRecordType.CheckpointBegin:
                    if (record.Payload.Length != 20)
                        throw new CorruptionException($"Invalid CheckpointBegin payload at LSN {record.Lsn}.");
                    checkpointBegins.Add(record.Lsn);
                    break;

                case WalRecordType.CheckpointEnd:
                    if (record.Payload.Length != 8)
                        throw new CorruptionException($"Invalid CheckpointEnd payload at LSN {record.Lsn}.");
                    long beginLsn = BinaryPrimitives.ReadInt64LittleEndian(record.Payload.Span);
                    if (!checkpointBegins.Contains(beginLsn))
                        throw new CorruptionException(
                            $"CheckpointEnd at LSN {record.Lsn} does not match a preceding CheckpointBegin.");
                    redoStartLsn = record.Lsn;
                    break;
            }
        }

        return new WalRecoveryScanResult(
            winners,
            redoStartLsn,
            lastLsn,
            maxObservedTxId,
            minObservedTxId);
    }
}

internal sealed record WalRecoveryScanResult(
    HashSet<long> Winners,
    long RedoStartLsn,
    long LastLsn,
    long MaxObservedTxId,
    long? MinObservedTxId);
