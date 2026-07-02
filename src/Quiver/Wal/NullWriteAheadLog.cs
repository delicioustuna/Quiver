using Quiver.Core;

namespace Quiver.Storage.Wal;

/// <summary>
/// レコードを永続化せず、LSN の採番だけを行うインメモリ用 WAL。
/// </summary>
internal sealed class NullWriteAheadLog : IWriteAheadLog
{
    private long _currentLsn;
    private long _bytesWritten;

    public long CurrentLsn => Volatile.Read(ref _currentLsn);
    public long FlushedLsn => CurrentLsn;
    public long BytesWritten => Volatile.Read(ref _bytesWritten);

    public long Append(WalRecordType type, TransactionId tx, ReadOnlySpan<byte> payload)
    {
        Interlocked.Add(ref _bytesWritten, payload.Length);
        return Interlocked.Increment(ref _currentLsn);
    }

    public void FlushTo(long lsn)
    {
        // 永続化先がないため常に flush 済みとして扱う。
    }


    public void BufferPageImage(TransactionId tx, byte fileKind, long pageId, byte[] payload)
    {
        // ページ本体は InMemoryPagedFile に保持されるため WAL 側には複製しない。
    }

    public void EvictCoalescedPageImagesFor(TransactionId tx)
    {
    }

    public long WriteCheckpoint(long oldestActiveLsn, long lastFlushedDataLsn)
        => CurrentLsn;

    public long WriteCheckpointBegin(long oldestActiveLsn, int dirtyPageCount)
        => CurrentLsn;

    public long WriteCheckpointEnd(long beginLsn)
        => CurrentLsn;

    public long WriteFileTruncate(byte fileKind, long newPageCount)
        => CurrentLsn;

    public void Truncate(long uptoLsn)
    {
    }

    public IWalReader OpenReader(long startLsn)
        => new NullWalReader();

    public void Dispose()
    {
    }

    private sealed class NullWalReader : IWalReader
    {
        public bool TryReadNext(out WalRecord record)
        {
            record = default;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
