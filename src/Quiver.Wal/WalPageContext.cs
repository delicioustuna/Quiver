using System.Buffers.Binary;
using Quiver.Core;

namespace Quiver.Wal;

/// <summary>
/// Thread-local WAL context for page image logging during write transactions.
/// Set when a write transaction begins; cleared at Commit/Abort.
/// </summary>
public static class WalPageContext
{
    [ThreadStatic]
    internal static WriteTransactionContext? Current;

    public static void Begin(IWriteAheadLog wal, TransactionId txId)
        => Current = new WriteTransactionContext(wal, txId);

    public static void End() => Current = null;

    /// <summary>
    /// If a write transaction is active on this thread, append a PageImage WAL record.
    /// Returns the WAL LSN of the appended record, or -1 when no context is set.
    /// </summary>
    public static long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => Current is { } ctx ? ctx.LogPageImage(fileKind, pageId, pageBytes) : -1L;
}

internal sealed class WriteTransactionContext(IWriteAheadLog wal, TransactionId txId)
{
    private readonly IWriteAheadLog _wal = wal;
    private readonly TransactionId _txId = txId;

    /// <summary>
    /// Encodes and appends a PageImage WAL record.
    /// Payload format: [version=1:1][fileKind:1][pageId:8][pageBytes:N]
    /// </summary>
    public long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var payload = new byte[10 + pageBytes.Length];
        payload[0] = 1;
        payload[1] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(2), pageId);
        pageBytes.CopyTo(payload.AsSpan(10));
        return _wal.Append(WalRecordType.PageImage, _txId, payload);
    }
}
