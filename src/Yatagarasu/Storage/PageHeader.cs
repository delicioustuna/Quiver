using System.Buffers.Binary;
using Yatagarasu.Core;

namespace Yatagarasu.Storage;

/// <summary>
/// 各ページ先頭 40 バイトのヘッダ定義。
/// </summary>
internal static class PageHeader
{
    private static ReadOnlySpan<byte> FamilyMagic => "QUIVER-SW"u8;

    public const int Size = 40;
    public const byte FamilyVersion = StorageFormatVersion.Current;

    private const int OffsetMagic = 0;
    private const int OffsetVersion = 9;
    private const int OffsetKind = 10;
    private const int OffsetReservedBeforePageId = 11;
    private const int OffsetPageId = 16;
    private const int OffsetLsn = 24;
    private const int OffsetChecksum = 32;
    private const int OffsetReservedAfterChecksum = 36;

    public static void Write(Span<byte> page, PageId pageId, PageKind kind, long lsn)
    {
        FamilyMagic.CopyTo(page[OffsetMagic..]);
        page[OffsetVersion] = FamilyVersion;
        page[OffsetKind] = (byte)kind;
        page[OffsetReservedBeforePageId..OffsetPageId].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(page[OffsetPageId..], pageId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(page[OffsetLsn..], lsn);
        page[OffsetReservedAfterChecksum..Size].Clear();
        uint crc = ComputeChecksum(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page[OffsetChecksum..], crc);
    }

    public static void UpdateLsnAndChecksum(Span<byte> page, long lsn)
    {
        BinaryPrimitives.WriteInt64LittleEndian(page[OffsetLsn..], lsn);
        BinaryPrimitives.WriteUInt32LittleEndian(page[OffsetChecksum..], 0);
        uint crc = ComputeChecksum(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page[OffsetChecksum..], crc);
    }

    public static void Validate(ReadOnlySpan<byte> page, PageId expectedPageId)
    {
        if (!page[OffsetMagic..(OffsetMagic + FamilyMagic.Length)].SequenceEqual(FamilyMagic))
            throw new StorageFormatMismatchException("database", 0, FamilyVersion);

        byte version = page[OffsetVersion];
        if (version != FamilyVersion)
            throw new StorageFormatMismatchException("database", version, FamilyVersion);

        if (!IsZero(page[OffsetReservedBeforePageId..OffsetPageId])
            || !IsZero(page[OffsetReservedAfterChecksum..Size]))
        {
            throw new CorruptionException(
                $"Unsupported database page header extension on page {expectedPageId.Value}.");
        }

        long pageId = BinaryPrimitives.ReadInt64LittleEndian(page[OffsetPageId..]);
        if (pageId != expectedPageId.Value)
            throw new CorruptionException($"Page ID mismatch: expected {expectedPageId.Value}, got {pageId}");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(page[OffsetChecksum..]);
        // 一時的に 0 にしてチェックサム再計算
        Span<byte> mutable = stackalloc byte[Size];
        page[..Size].CopyTo(mutable);
        BinaryPrimitives.WriteUInt32LittleEndian(mutable[OffsetChecksum..], 0);

        // 本体部分(Size 以降)と一緒に計算
        // ここでは page 全体を対象とする(ヘッダのチェックサムフィールド除く)
        uint computedCrc = Crc32.HashToUInt32(mutable) ^ Crc32.HashToUInt32(page[Size..]);
        if (storedCrc != computedCrc)
            throw new CorruptionException($"Checksum mismatch on page {expectedPageId.Value}");
    }

    public static long ReadLsn(ReadOnlySpan<byte> page) =>
        BinaryPrimitives.ReadInt64LittleEndian(page[OffsetLsn..]);

    public static PageKind ReadKind(ReadOnlySpan<byte> page) =>
        (PageKind)page[OffsetKind];

    private static uint ComputeChecksum(ReadOnlySpan<byte> page)
    {
        // チェックサムフィールド自体は 0 扱いで計算
        Span<byte> header = stackalloc byte[Size];
        page[..Size].CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[OffsetChecksum..], 0);
        return Crc32.HashToUInt32(header) ^ Crc32.HashToUInt32(page[Size..]);
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0) return false;
        }

        return true;
    }
}
