using System.Buffers.Binary;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

/// <summary>24byte entity version recordのpaged-file実装です。</summary>
internal sealed class EntityVersionStore : IEntityVersionStore
{
    public const int RecordSize = EntityVersionMeta.Size;
    public static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaAnyReuse = 8;
    private const int MetaFormatVersion = 31;
    internal const byte SidecarFormatVersion = 4;
    private const int OffsetXmin = 0;
    private const int OffsetXmax = 8;
    private const int OffsetGeneration = 16;

    private readonly IPagedFile _file;
    private bool _disposed;
    private bool _anyReuse;

    public EntityVersionStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            InitializeHeader();
        }
        else
        {
            CheckFormatVersion();
        }

        using var header = _file.PinForRead(HeaderPageId);
        _anyReuse = header.Data[MetaAnyReuse] != 0;
    }

    public bool AnyGenerationReuse => _anyReuse;

    public void MarkGenerationReuse()
    {
        if (_anyReuse) return;
        _anyReuse = true;
        var header = _file.PinForWrite(HeaderPageId);
        header.Data[MetaAnyReuse] = 1;
        _file.UnpinDirty(HeaderPageId, 0);
    }

    public EntityVersionMeta Read(long localId)
    {
        if (localId < 0) return EntityVersionMeta.Unset;
        (PageId pageId, int offset) = Location(localId);
        if (pageId.Value >= _file.PageCount) return EntityVersionMeta.Unset;
        using var handle = _file.PinForRead(pageId);
        ReadOnlySpan<byte> record = handle.Data.Slice(offset, RecordSize);
        var metadata = new EntityVersionMeta(
            BinaryPrimitives.ReadInt64LittleEndian(record[OffsetXmin..]),
            BinaryPrimitives.ReadInt64LittleEndian(record[OffsetXmax..]),
            BinaryPrimitives.ReadInt64LittleEndian(record[OffsetGeneration..]));
        return metadata.IsUnset ? EntityVersionMeta.Unset : metadata;
    }

    public void Write(long localId, in EntityVersionMeta meta)
    {
        if (localId < 0) throw new ArgumentOutOfRangeException(nameof(localId));
        (PageId pageId, int offset) = Location(localId);
        EnsurePage(pageId);
        var handle = _file.PinForWrite(pageId);
        Span<byte> record = handle.Data.Slice(offset, RecordSize);
        BinaryPrimitives.WriteInt64LittleEndian(record[OffsetXmin..], meta.Xmin);
        BinaryPrimitives.WriteInt64LittleEndian(record[OffsetXmax..], meta.Xmax);
        BinaryPrimitives.WriteInt64LittleEndian(record[OffsetGeneration..], meta.Generation);
        _file.UnpinDirty(pageId, 0);
    }

    public void UpdateXmax(long localId, long xmax)
        => UpdateField(localId, OffsetXmax, xmax);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }

    private void UpdateField(long localId, int fieldOffset, long value)
    {
        if (localId < 0) throw new ArgumentOutOfRangeException(nameof(localId));
        (PageId pageId, int offset) = Location(localId);
        EnsurePage(pageId);
        var handle = _file.PinForWrite(pageId);
        BinaryPrimitives.WriteInt64LittleEndian(
            handle.Data.Slice(offset + fieldOffset, sizeof(long)),
            value);
        _file.UnpinDirty(pageId, 0);
    }

    private static (PageId PageId, int Offset) Location(long localId)
    {
        int recordsPerPage = RecordsPerPage;
        return (
            new PageId(localId / recordsPerPage + 2),
            checked((int)(localId % recordsPerPage) * RecordSize));
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.VertexRecord);
    }

    private void InitializeHeader()
    {
        var header = _file.PinForWrite(HeaderPageId);
        header.Data[MetaFormatVersion] = SidecarFormatVersion;
        _file.UnpinDirty(HeaderPageId, 0);
    }

    private void CheckFormatVersion()
    {
        using var header = _file.PinForRead(HeaderPageId);
        byte actual = header.Data[MetaFormatVersion];
        if (actual != SidecarFormatVersion)
            throw new StorageFormatMismatchException(
                "entity-version-meta",
                actual,
                SidecarFormatVersion);
    }
}
