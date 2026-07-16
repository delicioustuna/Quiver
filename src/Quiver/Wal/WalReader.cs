using System.Buffers.Binary;
using Quiver.Core;

namespace Quiver.Storage.Wal;

/// <summary>
/// <c>QUIVER-SW</c> WAL のシーケンシャルリーダ。
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
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            WalFormat.ValidateAndPosition(_stream);
        }
    }

    public bool TryReadNext(out WalRecord record)
    {
        record = default;
        while (true)
        {
            if (_disposed || _stream == null) return false;

            if (!TryReadRecord(_stream, out record))
                return false;

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

        if (fs.Position == fs.Length) return false;
        if (fs.Length - fs.Position < hs)
            throw new CorruptionException("Truncated WAL record header.");

        Span<byte> hdr = stackalloc byte[hs];
        fs.ReadExactly(hdr);

        int length = BinaryPrimitives.ReadInt32LittleEndian(hdr);
        if (length < hs || length > hs + WriteAheadLog.MaxPayloadSize)
            throw new CorruptionException($"Invalid WAL record length {length}.");

        long lsn = BinaryPrimitives.ReadInt64LittleEndian(hdr[4..]);
        long txId = BinaryPrimitives.ReadInt64LittleEndian(hdr[12..]);
        var type = (WalRecordType)hdr[20];
        if (!WalFormat.IsKnownRecordType(type))
            throw new CorruptionException($"Unknown WAL record type 0x{hdr[20]:X2}.");
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(hdr[21..]);

        int payloadLen = length - hs;
        byte[] payload = payloadLen > 0 ? new byte[payloadLen] : [];
        if (payloadLen > 0)
        {
            if (fs.Length - fs.Position < payloadLen)
                throw new CorruptionException("Truncated WAL record payload.");
            fs.ReadExactly(payload);
        }

        var crc = new Crc32();
        crc.Append(hdr[..21]);
        if (payloadLen > 0) crc.Append(payload);
        if (crc.GetCurrentHashAsUInt32() != storedCrc)
            throw new CorruptionException($"WAL checksum mismatch at LSN {lsn}.");

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
