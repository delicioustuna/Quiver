using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

internal readonly record struct EdgeLocator(
    int Generation,
    bool Live,
    long RecordSequence,
    VertexId Source,
    VertexId Target,
    EdgeTypeId Type);

internal sealed class EdgeLocatorStore
{
    private const int RecordSize = 32;
    private static readonly PageId HeaderPageId = new(1);
    private const int MetaHwm = 0;
    private const int MetaFormatVersion = 31;
    private const byte LayoutVersion = 1;

    private const byte FlagLive = 0x01;
    private const int OffsetGeneration = 0;
    private const int OffsetFlags = 4;
    private const int OffsetRecordSequence = 8;
    private const int OffsetSource = 16;
    private const int OffsetTarget = 22;
    private const int OffsetType = 28;

    private readonly IPagedFile _file;
    private long _hwm;

    public EdgeLocatorStore(IPagedFile file)
    {
        _file = file;
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

    public long Hwm => _hwm;

    public bool TryRead(long sequence, out EdgeLocator locator)
    {
        locator = default;
        if (sequence < 0 || sequence >= _hwm)
            return false;

        var (pageId, offset) = Location(sequence);
        if (pageId.Value >= _file.PageCount)
            return false;

        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(offset, RecordSize);
        int generation = BinaryPrimitives.ReadInt32LittleEndian(rec[OffsetGeneration..]);
        long recordSequence = BinaryPrimitives.ReadInt64LittleEndian(rec[OffsetRecordSequence..]);
        if (generation == 0 && recordSequence == 0 && rec[OffsetFlags] == 0)
            return false;

        locator = new EdgeLocator(
            generation,
            (rec[OffsetFlags] & FlagLive) != 0,
            recordSequence,
            new VertexId(RecordHelpers.ReadInt48(rec[OffsetSource..])),
            new VertexId(RecordHelpers.ReadInt48(rec[OffsetTarget..])),
            new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(rec[OffsetType..])));
        return true;
    }

    public bool TryResolve(EdgeId id, out EdgeLocator locator)
    {
        if (!TryRead(id.Sequence, out locator))
            return false;
        if (!locator.Live)
            return false;
        int carriedGeneration = id.Generation;
        if (carriedGeneration != 0 && carriedGeneration != locator.Generation)
            return false;
        return locator.RecordSequence >= 0;
    }

    public void WriteLive(
        long sequence,
        int generation,
        long recordSequence,
        VertexId source,
        VertexId target,
        EdgeTypeId type)
    {
        Write(sequence, generation, FlagLive, recordSequence, source, target, type);
    }

    public void WriteDeleted(long sequence, int generation)
    {
        TryRead(sequence, out var existing);
        Write(
            sequence,
            generation,
            flags: 0,
            existing.RecordSequence >= 0 ? existing.RecordSequence : sequence,
            existing.Source,
            existing.Target,
            existing.Type);
    }

    public void ReloadMeta() => LoadMeta();

    private void Write(
        long sequence,
        int generation,
        byte flags,
        long recordSequence,
        VertexId source,
        VertexId target,
        EdgeTypeId type)
    {
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        var (pageId, offset) = Location(sequence);
        EnsurePage(pageId);
        using var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(offset, RecordSize);
        rec.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(rec[OffsetGeneration..], generation);
        rec[OffsetFlags] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(rec[OffsetRecordSequence..], recordSequence);
        RecordHelpers.WriteInt48(rec[OffsetSource..], source.Sequence);
        RecordHelpers.WriteInt48(rec[OffsetTarget..], target.Sequence);
        BinaryPrimitives.WriteInt16LittleEndian(rec[OffsetType..], (short)type.Value);

        if (sequence >= _hwm)
        {
            _hwm = sequence + 1;
            SaveMeta();
        }
    }

    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize;

    private (PageId pageId, int offset) Location(long sequence)
        => (new PageId(sequence / RecordsPerPage + 2), (int)(sequence % RecordsPerPage) * RecordSize);

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.ItemPointerMap);
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
    }

    private void SaveMeta(bool initialise = false)
    {
        using var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        if (initialise)
            ph.Data[MetaFormatVersion] = LayoutVersion;
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte version = h.Data[MetaFormatVersion];
        if (version != LayoutVersion)
            throw new StorageFormatMismatchException("edge-locator", version, LayoutVersion);
    }
}
