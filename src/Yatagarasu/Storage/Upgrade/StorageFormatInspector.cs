using Yatagarasu.Core;

namespace Yatagarasu.Storage.Upgrade;

internal static class StorageFormatInspector
{
    private static ReadOnlySpan<byte> FamilyMagic => "QUIVER-SW"u8;
    private const int VersionOffset = 9;
    private const int MinimumHeaderLength = VersionOffset + 1;

    internal static byte Inspect(string filePath)
    {
        using var stream = OpenExclusiveRead(filePath);
        return Inspect(stream);
    }

    internal static FileStream OpenExclusiveRead(string filePath)
        => new(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

    internal static byte Inspect(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("Storage inspection requires a readable, seekable stream.", nameof(stream));

        long originalPosition = stream.Position;
        try
        {
            if (stream.Length < MinimumHeaderLength)
                throw new CorruptionException(
                    $"Database header is truncated ({stream.Length} bytes).");

            Span<byte> prefix = stackalloc byte[MinimumHeaderLength];
            stream.Position = 0;
            stream.ReadExactly(prefix);
            if (!prefix[..FamilyMagic.Length].SequenceEqual(FamilyMagic))
                throw new StorageFormatMismatchException(
                    "database", found: 0, StorageFormatVersion.Current);

            byte version = prefix[VersionOffset];
            if (version == StorageFormatVersion.Current)
            {
                if (stream.Length < PagedFile.PageSizeConst)
                    throw new CorruptionException(
                        $"Current database header page is truncated ({stream.Length} bytes).");

                byte[] page = new byte[PagedFile.PageSizeConst];
                stream.Position = 0;
                stream.ReadExactly(page);
                PageHeader.Validate(page, new PageId(0));
            }

            return version;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }
}
