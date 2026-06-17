using System.Buffers.Binary;
using System.IO.Hashing;
using Quiver.Core;

namespace Quiver.Storage.Wal;

/// <summary>
/// 単一ファイル WAL のシーケンシャルリーダ。先頭から順に読み、
/// <see cref="WalRecordType.EndOfSegment"/> マーカ (旧形式の残骸) と LSN &lt; startLsn の
/// レコードはスキップする。
/// </summary>
internal sealed class WalReader : IWalReader
{
    private readonly long _startLsn;
    private FileStream? _stream;
    private bool _disposed;

    internal WalReader(string path, long startLsn)
    {
        _startLsn = startLsn;
        if (File.Exists(path))
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    public bool TryReadNext(out WalRecord record)
    {
        record = default;
        while (true)
        {
            if (_disposed || _stream == null) return false;

            if (!TryReadRecord(_stream, out record))
                return false;

            if (record.Type == WalRecordType.EndOfSegment) continue;
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

    // WalReader と WriteAheadLog.RebuildState / Truncate で共有するヘルパ
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
