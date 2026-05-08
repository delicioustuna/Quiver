using System.Buffers;
using System.IO.Hashing;
using System.Buffers.Binary;
using System.Threading.Channels;
using GraphDb.Engine.Core;

namespace GraphDb.Engine.Wal;

/// <summary>
/// グループコミット対応の WAL 実装スケルトン。
/// </summary>
public sealed class WriteAheadLog : IWriteAheadLog
{
    // WAL レコードヘッダサイズ: Length(4) + Lsn(8) + TxId(8) + Type(1) + Checksum(4) = 25
    private const int HeaderSize = 25;
    private const long DefaultSegmentSize = 64 * 1024 * 1024;
    private const int WriteBufferSize = 1024 * 1024;

    private readonly string _directory;
    private readonly long _segmentSize;
    private readonly object _writeLock = new();
    private readonly Channel<FlushRequest> _flushChannel;

    private long _currentLsn;
    private long _flushedLsn;
    private long _currentSegment;
    private FileStream? _currentSegmentStream;
    private bool _disposed;

    public long CurrentLsn => Volatile.Read(ref _currentLsn);
    public long FlushedLsn => Volatile.Read(ref _flushedLsn);

    public WriteAheadLog(string directory, long segmentSize = DefaultSegmentSize)
    {
        _directory = directory;
        _segmentSize = segmentSize;
        _flushChannel = Channel.CreateUnbounded<FlushRequest>();
        Directory.CreateDirectory(directory);
        OpenCurrentSegment();
        _ = RunFlushLoopAsync();
    }

    public long Append(WalRecordType type, TransactionId tx, ReadOnlySpan<byte> payload) =>
        throw new NotImplementedException();

    public void FlushTo(long lsn) => throw new NotImplementedException();

    public long WriteCheckpoint(long oldestActiveLsn, long lastFlushedDataLsn) =>
        throw new NotImplementedException();

    public void Truncate(long uptoLsn) => throw new NotImplementedException();

    public IWalReader OpenReader(long startLsn) => throw new NotImplementedException();

    private void OpenCurrentSegment()
    {
        string path = SegmentPath(_currentSegment);
        _currentSegmentStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    }

    private string SegmentPath(long segment) =>
        Path.Combine(_directory, $"wal.{segment:D8}.log");

    private async Task RunFlushLoopAsync()
    {
        await foreach (var req in _flushChannel.Reader.ReadAllAsync())
        {
            // グループコミット実装予定
            _ = req;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushChannel.Writer.Complete();
        _currentSegmentStream?.Flush(flushToDisk: true);
        _currentSegmentStream?.Dispose();
    }

    private readonly record struct FlushRequest(long TargetLsn, TaskCompletionSource<long> Tcs);
}
