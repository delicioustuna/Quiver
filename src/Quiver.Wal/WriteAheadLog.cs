using System.Buffers.Binary;
using System.IO.Hashing;
using System.Threading.Channels;
using Quiver.Core;

namespace Quiver.Wal;

public sealed class WriteAheadLog : IWriteAheadLog
{
    // Header layout: Length(4) + Lsn(8) + TxId(8) + Type(1) + Crc32C(4) = 25 bytes
    internal const int HeaderSize = 25;
    private const long DefaultSegmentCapacity = 64L * 1024 * 1024;
    private const int WriteBufferSize = 1024 * 1024;
    internal const int MaxPayloadSize = 8 * 1024 * 1024;

    private readonly string _directory;
    private readonly long _segmentCapacity;
    private readonly object _writeLock = new();
    private readonly Channel<FlushRequest> _flushChannel;
    private readonly Task _flushTask;

    private long _nextLsn;
    private long _flushedLsn = -1;
    private long _currentSegIdx;
    private long _segBytesUsed;
    private FileStream? _segStream;
    private readonly byte[] _buffer = new byte[WriteBufferSize];
    private int _bufPos;
    private bool _disposed;

    // segIdx → first LSN of that segment (rebuilt on open, updated on segment roll)
    private readonly SortedDictionary<long, long> _segFirstLsn = new();

    public long CurrentLsn => Volatile.Read(ref _nextLsn) - 1;
    public long FlushedLsn => Volatile.Read(ref _flushedLsn);

    public WriteAheadLog(string directory, long segmentCapacity = DefaultSegmentCapacity)
    {
        _directory = directory;
        _segmentCapacity = segmentCapacity;
        Directory.CreateDirectory(directory);
        _flushChannel = Channel.CreateUnbounded<FlushRequest>(
            new UnboundedChannelOptions { SingleReader = true });
        RebuildState();
        _segStream = OpenSegStream(_currentSegIdx, append: true);
        _segBytesUsed = _segStream.Position;
        _flushTask = Task.Factory.StartNew(
            RunFlushLoopAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public long Append(WalRecordType type, TransactionId tx, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new StorageException($"WAL payload size {payload.Length} exceeds max {MaxPayloadSize}");

        int recordSize = HeaderSize + payload.Length;

        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_segBytesUsed + _bufPos + recordSize > _segmentCapacity)
            {
                WriteMarkerLocked(WalRecordType.EndOfSegment);
                FlushBufferLocked();
                RollSegmentLocked();
            }
            else if (_bufPos + recordSize > WriteBufferSize)
            {
                FlushBufferLocked();
            }

            long lsn = _nextLsn++;
            _segFirstLsn.TryAdd(_currentSegIdx, lsn);
            WriteRecordToBuffer(lsn, type, tx.Value, payload);
            return lsn;
        }
    }

    public void FlushTo(long lsn)
    {
        if (Volatile.Read(ref _flushedLsn) >= lsn) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_flushChannel.Writer.TryWrite(new FlushRequest(lsn, tcs)))
        {
            // Channel completed (Dispose called) — check once more
            if (Volatile.Read(ref _flushedLsn) >= lsn) return;
            throw new ObjectDisposedException(nameof(WriteAheadLog));
        }
        tcs.Task.GetAwaiter().GetResult();
    }

    public long WriteCheckpoint(long oldestActiveLsn, long lastFlushedDataLsn)
    {
        Span<byte> payload = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(payload, oldestActiveLsn);
        BinaryPrimitives.WriteInt64LittleEndian(payload[8..], lastFlushedDataLsn);
        long lsn = Append(WalRecordType.Checkpoint, new TransactionId(-1), payload);
        FlushTo(lsn);
        return lsn;
    }

    public void Truncate(long uptoLsn)
    {
        lock (_writeLock)
        {
            var keys = _segFirstLsn.Keys.ToList();
            for (int i = 0; i < keys.Count - 1; i++)
            {
                long segIdx = keys[i];
                if (segIdx == _currentSegIdx) continue;
                long nextFirst = _segFirstLsn[keys[i + 1]];
                // All records in segIdx have LSN < nextFirst; safe to delete if nextFirst-1 <= uptoLsn
                if (nextFirst - 1 <= uptoLsn)
                {
                    try { File.Delete(SegmentPath(segIdx)); } catch { }
                    _segFirstLsn.Remove(segIdx);
                }
            }
        }
    }

    public IWalReader OpenReader(long startLsn)
    {
        long[] segIndices;
        lock (_writeLock)
        {
            // Find the last segment whose firstLsn <= startLsn
            long startSeg = _currentSegIdx;
            foreach (var (segIdx, firstLsn) in _segFirstLsn)
            {
                if (firstLsn <= startLsn) startSeg = segIdx;
                else break;
            }
            segIndices = [.. _segFirstLsn.Keys.Where(k => k >= startSeg).OrderBy(k => k)];
        }
        return new WalReader(_directory, segIndices, startLsn);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushChannel.Writer.TryComplete();
        try { _flushTask.GetAwaiter().GetResult(); } catch { }
        lock (_writeLock)
        {
            FlushBufferLocked();
            _segStream?.Flush(flushToDisk: true);
            _segStream?.Dispose();
            _segStream = null;
        }
    }

    // -----------------------------------------------------------------------
    // Write helpers (all called under _writeLock)
    // -----------------------------------------------------------------------

    private void WriteRecordToBuffer(long lsn, WalRecordType type, long txId, ReadOnlySpan<byte> payload)
    {
        int recordSize = HeaderSize + payload.Length;
        Span<byte> dest = _buffer.AsSpan(_bufPos, recordSize);

        BinaryPrimitives.WriteInt32LittleEndian(dest, recordSize);
        BinaryPrimitives.WriteInt64LittleEndian(dest[4..], lsn);
        BinaryPrimitives.WriteInt64LittleEndian(dest[12..], txId);
        dest[20] = (byte)type;
        if (payload.Length > 0) payload.CopyTo(dest[HeaderSize..]);

        // CRC32 over bytes[0..21) (header without checksum field) + payload
        var crc = new Crc32();
        crc.Append(dest[..21]);
        if (payload.Length > 0) crc.Append(dest[HeaderSize..]);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[21..], crc.GetCurrentHashAsUInt32());

        _bufPos += recordSize;
    }

    private void WriteMarkerLocked(WalRecordType type)
    {
        if (_bufPos + HeaderSize > WriteBufferSize)
            FlushBufferLocked();
        // Marker: lsn=-1, txId=-1, no payload
        WriteRecordToBuffer(-1L, type, -1L, ReadOnlySpan<byte>.Empty);
    }

    private void FlushBufferLocked()
    {
        if (_bufPos == 0 || _segStream == null) return;
        _segStream.Write(_buffer, 0, _bufPos);
        _segBytesUsed += _bufPos;
        _bufPos = 0;
    }

    private void RollSegmentLocked()
    {
        _segStream?.Dispose();
        _currentSegIdx++;
        _segBytesUsed = 0;
        _segStream = OpenSegStream(_currentSegIdx, append: false);
    }

    // -----------------------------------------------------------------------
    // Flush loop (background task — group commit)
    // -----------------------------------------------------------------------

    private async Task RunFlushLoopAsync()
    {
        var pending = new List<FlushRequest>();
        while (await _flushChannel.Reader.WaitToReadAsync())
        {
            while (_flushChannel.Reader.TryRead(out var req))
                pending.Add(req);

            long highestLsn;
            lock (_writeLock)
            {
                FlushBufferLocked();
                _segStream?.Flush(flushToDisk: true);
                highestLsn = _nextLsn > 0 ? _nextLsn - 1 : -1;
                Volatile.Write(ref _flushedLsn, highestLsn);
            }

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].TargetLsn <= highestLsn)
                {
                    pending[i].Tcs.TrySetResult(true);
                    pending.RemoveAt(i);
                }
            }
        }

        foreach (var req in pending)
            req.Tcs.TrySetCanceled();
    }

    // -----------------------------------------------------------------------
    // Segment helpers
    // -----------------------------------------------------------------------

    private FileStream OpenSegStream(long segIdx, bool append)
    {
        string path = SegmentPath(segIdx);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (append) stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    internal string SegmentPath(long segIdx) =>
        Path.Combine(_directory, $"wal.{segIdx:D8}.log");

    private void RebuildState()
    {
        _segFirstLsn.Clear();
        var segIndices = Directory.GetFiles(_directory, "wal.????????.log")
            .Select(f => long.Parse(Path.GetFileNameWithoutExtension(f)[4..]))
            .OrderBy(x => x)
            .ToList();

        if (segIndices.Count == 0)
        {
            _currentSegIdx = 0;
            _nextLsn = 0;
            return;
        }

        long maxLsn = -1;
        foreach (long segIdx in segIndices)
        {
            long firstLsn = -1;
            try
            {
                using var fs = new FileStream(SegmentPath(segIdx),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                while (WalReader.TryReadRecord(fs, out WalRecord rec))
                {
                    if (rec.Type == WalRecordType.EndOfSegment) break;
                    if (firstLsn < 0) firstLsn = rec.Lsn;
                    if (rec.Lsn > maxLsn) maxLsn = rec.Lsn;
                }
            }
            catch { }

            if (firstLsn >= 0) _segFirstLsn[segIdx] = firstLsn;
            _currentSegIdx = segIdx;
        }

        _nextLsn = maxLsn >= 0 ? maxLsn + 1 : 0;
    }

    private readonly record struct FlushRequest(long TargetLsn, TaskCompletionSource<bool> Tcs);
}
