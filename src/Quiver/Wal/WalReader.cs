using System.Buffers.Binary;
using System.IO.Hashing;
using Quiver.Core;

namespace Quiver.Storage.Wal;

internal sealed class WalReader : IWalReader
{
    private readonly string _directory;
    private readonly long[] _segmentIndices;
    private readonly long _startLsn;
    private int _segArrIdx;
    private FileStream? _stream;
    private bool _disposed;

    internal WalReader(string directory, long[] sortedSegmentIndices, long startLsn)
    {
        _directory = directory;
        _segmentIndices = sortedSegmentIndices;
        _startLsn = startLsn;
        if (sortedSegmentIndices.Length > 0)
            OpenCurrentSegment();
    }

    public bool TryReadNext(out WalRecord record)
    {
        record = default;
        while (true)
        {
            if (_disposed || _stream == null) return false;

            if (!TryReadRecord(_stream, out record))
            {
                if (!AdvanceToNextSegment()) return false;
                continue;
            }

            if (record.Type == WalRecordType.EndOfSegment)
            {
                if (!AdvanceToNextSegment()) return false;
                continue;
            }

            if (record.Lsn < _startLsn) continue;

            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream?.Dispose();
        _stream = null;
    }

    private void OpenCurrentSegment()
    {
        _stream?.Dispose();
        string path = SegmentPath(_segmentIndices[_segArrIdx]);
        if (!File.Exists(path)) { _stream = null; return; }
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private bool AdvanceToNextSegment()
    {
        _segArrIdx++;
        if (_segArrIdx >= _segmentIndices.Length) return false;
        OpenCurrentSegment();
        return _stream != null;
    }

    private string SegmentPath(long segIdx) =>
        Path.Combine(_directory, $"wal.{segIdx:D8}.log");

    // WalReader と WriteAheadLog.RebuildState で共有するヘルパ
    internal static bool TryReadRecord(FileStream fs, out WalRecord record)
    {
        record = default;
        const int hs = WriteAheadLog.HeaderSize;

        Span<byte> hdr = stackalloc byte[hs];
        try { fs.ReadExactly(hdr); }
        catch (EndOfStreamException) { return false; }

        int length = BinaryPrimitives.ReadInt32LittleEndian(hdr);
        if (length < hs || length > hs + WriteAheadLog.MaxPayloadSize) return false;

        long lsn = BinaryPrimitives.ReadInt64LittleEndian(hdr[4..]);
        long txId = BinaryPrimitives.ReadInt64LittleEndian(hdr[12..]);
        var type = (WalRecordType)hdr[20];
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(hdr[21..]);

        int payloadLen = length - hs;
        byte[] payload = payloadLen > 0 ? new byte[payloadLen] : [];
        if (payloadLen > 0)
        {
            try { fs.ReadExactly(payload); }
            catch (EndOfStreamException) { return false; }
        }

        var crc = new Crc32();
        crc.Append(hdr[..21]);
        if (payloadLen > 0) crc.Append(payload);
        if (crc.GetCurrentHashAsUInt32() != storedCrc) return false;

        record = new WalRecord
        {
            Lsn = lsn,
            Type = type,
            TransactionId = new TransactionId(txId),
            Payload = payload,
        };
        return true;
    }
}
