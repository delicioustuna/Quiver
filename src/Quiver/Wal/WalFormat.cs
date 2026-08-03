using Quiver.Core;

namespace Quiver.Storage.Wal;

internal static class WalFormat
{
    private static ReadOnlySpan<byte> FamilyMagic => "QUIVER-SW"u8;

    internal const int FileHeaderSize = 16;
    internal const byte FamilyVersion = StorageFormatVersion.Current;
    private const byte WalKind = (byte)'W';
    private const int HeaderExtensionOffset = 11;

    internal static void Initialize(FileStream stream)
    {
        if (stream.Length != 0)
        {
            ValidateAndPosition(stream);
            return;
        }

        Span<byte> header = stackalloc byte[FileHeaderSize];
        header.Clear();
        FamilyMagic.CopyTo(header);
        header[9] = WalKind;
        header[10] = FamilyVersion;
        stream.Write(header);
        stream.Flush(flushToDisk: true);
        stream.Position = FileHeaderSize;
    }

    internal static void ValidateAndPosition(FileStream stream)
    {
        if (stream.Length < FileHeaderSize)
            throw new WalFormatMismatchException(
                $"truncated header ({stream.Length} bytes)",
                $"QUIVER-SW/WAL v{FamilyVersion}");

        Span<byte> header = stackalloc byte[FileHeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..FamilyMagic.Length].SequenceEqual(FamilyMagic)
            || header[9] != WalKind
            || header[10] != FamilyVersion)
        {
            throw new WalFormatMismatchException(
                "unknown or legacy family",
                $"QUIVER-SW/WAL v{FamilyVersion}");
        }

        if (!IsZero(header[HeaderExtensionOffset..]))
        {
            throw new WalFormatMismatchException(
                $"QUIVER-SW/WAL v{FamilyVersion} with an unsupported header extension",
                $"QUIVER-SW/WAL v{FamilyVersion} without extensions");
        }

        stream.Position = FileHeaderSize;
    }

    internal static bool IsKnownRecordType(WalRecordType type)
        => type is WalRecordType.BeginWrite
            or WalRecordType.PageImage
            or WalRecordType.Commit
            or WalRecordType.Abort
            or WalRecordType.CheckpointBegin
            or WalRecordType.CheckpointEnd
            or WalRecordType.FileTruncate;

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0) return false;
        }

        return true;
    }
}
