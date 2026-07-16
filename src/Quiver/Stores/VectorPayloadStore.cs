using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// property が参照する immutable vector payload の primary store。
/// metadata は generation、element type、dimensions、byte length、checksum を保持し、
/// 要素列は blob tenant に格納する。
/// </summary>
internal sealed class VectorPayloadStore
{
    private const int RecordSize = 32;
    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize;
    private const byte FlagPresent = 0x01;
    private const int OffFlags = 0;
    private const int OffElementType = 1;
    private const int OffGeneration = 4;
    private const int OffDimensions = 8;
    private const int OffByteLength = 12;
    private const int OffChecksum = 16;
    private const int OffBlobId = 20;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaHwm = 0;
    private const int MetaFormatVersion = 31;

    private readonly IPagedFile _file;
    private readonly BlobStore _blobs;
    private long _hwm;

    public VectorPayloadStore(IPagedFile file, IPagedFile blobFile)
    {
        _file = file;
        _blobs = new BlobStore(blobFile);
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _hwm = 0;
            SaveMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
        }
    }

    public VectorPayloadRef Write(ReadOnlySpan<float> elements)
    {
        if (elements.IsEmpty)
            throw new VectorException("vector payload dimensions must be positive.");

        long sequence = _hwm++;
        const int generation = 1;
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(elements);
        long blobId = _blobs.Write(bytes);
        var (pageId, offset) = Location(sequence);
        EnsurePage(pageId);
        var page = _file.PinForWrite(pageId);
        Span<byte> record = page.Data.Slice(offset, RecordSize);
        record.Clear();
        record[OffFlags] = FlagPresent;
        record[OffElementType] = (byte)VectorElementType.Float32;
        BinaryPrimitives.WriteInt32LittleEndian(record[OffGeneration..], generation);
        BinaryPrimitives.WriteInt32LittleEndian(record[OffDimensions..], elements.Length);
        BinaryPrimitives.WriteInt32LittleEndian(record[OffByteLength..], bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[OffChecksum..], Crc32.HashToUInt32(bytes));
        BinaryPrimitives.WriteInt64LittleEndian(record[OffBlobId..], blobId);
        _file.UnpinDirty(pageId, 0);
        SaveMeta();
        return new VectorPayloadRef(sequence, generation);
    }

    public float[] Read(VectorPayloadRef reference)
    {
        if (!reference.IsValid || reference.Sequence >= _hwm)
            throw new CorruptionException("Property references a missing vector payload.");

        var (pageId, offset) = Location(reference.Sequence);
        using var page = _file.PinForRead(pageId);
        ReadOnlySpan<byte> record = page.Data.Slice(offset, RecordSize);
        if ((record[OffFlags] & FlagPresent) == 0)
            throw new CorruptionException("Property references an absent vector payload.");
        int generation = BinaryPrimitives.ReadInt32LittleEndian(record[OffGeneration..]);
        if (generation != reference.Generation)
            throw new CorruptionException("Property references a stale vector payload generation.");
        if ((VectorElementType)record[OffElementType] != VectorElementType.Float32)
            throw new CorruptionException("Vector payload element type is unsupported.");

        int dimensions = BinaryPrimitives.ReadInt32LittleEndian(record[OffDimensions..]);
        int byteLength = BinaryPrimitives.ReadInt32LittleEndian(record[OffByteLength..]);
        if (dimensions <= 0 || byteLength != checked(dimensions * sizeof(float)))
            throw new CorruptionException("Vector payload dimensions and byte length do not match.");
        long blobId = BinaryPrimitives.ReadInt64LittleEndian(record[OffBlobId..]);
        if (_blobs.GetLength(blobId) != byteLength)
            throw new CorruptionException("Vector payload blob length does not match its metadata.");

        byte[] bytes = new byte[byteLength];
        if (_blobs.Read(blobId, bytes) != byteLength)
            throw new CorruptionException("Vector payload blob is truncated.");
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(record[OffChecksum..]);
        if (Crc32.HashToUInt32(bytes) != expected)
            throw new CorruptionException("Vector payload checksum mismatch.");
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    public IReadOnlyList<VectorPayloadRef> ScanOrphans(IReadOnlySet<VectorPayloadRef> reachable)
    {
        ArgumentNullException.ThrowIfNull(reachable);
        var orphans = new List<VectorPayloadRef>();
        for (long sequence = 0; sequence < _hwm; sequence++)
        {
            var (pageId, offset) = Location(sequence);
            using var page = _file.PinForRead(pageId);
            ReadOnlySpan<byte> record = page.Data.Slice(offset, RecordSize);
            if ((record[OffFlags] & FlagPresent) == 0)
                continue;
            var reference = new VectorPayloadRef(
                sequence,
                BinaryPrimitives.ReadInt32LittleEndian(record[OffGeneration..]));
            if (!reachable.Contains(reference))
                orphans.Add(reference);
        }
        return orphans;
    }

    public void ReloadMeta()
    {
        LoadMeta();
        _blobs.ReloadMeta();
    }

    private (PageId PageId, int Offset) Location(long sequence)
        => (new PageId(sequence / RecordsPerPage + 2), (int)(sequence % RecordsPerPage) * RecordSize);

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.PropertyRecord);
    }

    private void LoadMeta()
    {
        using var page = _file.PinForRead(HeaderPageId);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(page.Data[MetaHwm..]);
    }

    private void SaveMeta(bool initialise = false)
    {
        using var page = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(page.Data[MetaHwm..], _hwm);
        if (initialise)
            page.Data[MetaFormatVersion] = StorageFormatVersion.Current;
    }

    private void CheckFormatVersion()
    {
        using var page = _file.PinForRead(HeaderPageId);
        byte version = page.Data[MetaFormatVersion];
        if (version != StorageFormatVersion.Current)
            throw new StorageFormatMismatchException(
                "vector-payload", version, StorageFormatVersion.Current);
    }
}
