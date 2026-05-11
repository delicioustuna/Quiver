using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Stores;

// Property record layout (41 bytes):
//  0 Flags(1) | 1 KeyId(4) | 5 ValueType(1) | 6 InlineValue(24) | 30 SpilloverId(5) | 35 NextPropId(6)
internal sealed class PropertyStore : IPropertyStore
{
    public const int RecordSize = 41;
    private const byte FlagInUse = 0x01;
    private const byte FlagSpillover = 0x02;
    private const int InlineCapacity = 24;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;
    private const int MetaHwm = 8;

    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 199

    private readonly IPagedFile _file;
    private readonly BlobStore _blobs;
    private long _freeHead;
    private long _hwm;

    public PropertyStore(IPagedFile file, IPagedFile blobFile)
    {
        _blobs = new BlobStore(blobFile);
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _freeHead = -1; _hwm = 0;
            FlushMeta();
        }
        else
        {
            LoadMeta();
        }
    }

    public PropertyId Create(PropertyKeyId keyId, in PropertyValue value, PropertyId currentFirst)
    {
        long id;
        if (_freeHead >= 0)
        {
            id = _freeHead;
            var (fpid, foff) = Location(id);
            using var fh = _file.PinForRead(fpid);
            _freeHead = RecordHelpers.ReadInt48(fh.Data[(foff + 35)..]);
        }
        else
        {
            id = _hwm++;
        }

        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();

        bool spillover = value.Type is PropertyValueType.String or PropertyValueType.Bytes
            && value.EncodedSize > InlineCapacity;

        byte flags = FlagInUse;
        if (spillover) flags |= FlagSpillover;
        rec[0] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(rec[1..], keyId.Value);
        rec[5] = (byte)value.Type;
        RecordHelpers.WriteInt48(rec[35..], currentFirst.Value); // NextPropId = old head

        if (spillover)
        {
            long blobId = _blobs.Write(value.Type == PropertyValueType.String
                ? value.Utf8StringValue
                : value.BytesValue);
            RecordHelpers.WriteInt40(rec[30..], blobId);
        }
        else
        {
            WriteInline(rec[6..], value);
        }

        _file.UnpinDirty(wpid, 0);
        FlushMeta();
        return new PropertyId(id);
    }

    public PropertyId Delete(PropertyId propId, PropertyId currentFirst)
    {
        // Find record, free blob if spillover, unlink from chain
        var (pageId, off) = Location(propId.Value);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        bool spillover = (rec[0] & FlagSpillover) != 0;
        long nextId = RecordHelpers.ReadInt48(rec[35..]);
        if (spillover)
        {
            long blobId = RecordHelpers.ReadInt40(rec[30..]);
            _blobs.Free(blobId);
        }
        rec.Clear();
        RecordHelpers.WriteInt48(rec[35..], _freeHead); // chain into free list
        _file.UnpinDirty(pageId, 0);

        _freeHead = propId.Value;
        FlushMeta();

        // If propId was the head, the new head is nextId
        if (propId == currentFirst)
            return new PropertyId(nextId);

        // Otherwise scan chain to unlink
        PropertyId prev = currentFirst;
        while (prev.IsValid)
        {
            using var h = _file.PinForRead(Location(prev.Value).pageId);
            var (pid2, off2) = Location(prev.Value);
            using var hr = _file.PinForRead(pid2);
            long nx = RecordHelpers.ReadInt48(hr.Data[(off2 + 35)..]);
            if (nx == propId.Value)
            {
                // Patch prev.next = nextId
                var pw = _file.PinForWrite(pid2);
                RecordHelpers.WriteInt48(pw.Data[(off2 + 35)..], nextId);
                _file.UnpinDirty(pid2, 0);
                break;
            }
            prev = new PropertyId(nx);
        }

        return currentFirst;
    }

    public PropertyReadHandle Read(PropertyId propId)
    {
        var (pageId, off) = Location(propId.Value);
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        var keyId = new PropertyKeyId(BinaryPrimitives.ReadInt32LittleEndian(rec[1..]));
        var vtype = (PropertyValueType)rec[5];
        var nextId = new PropertyId(RecordHelpers.ReadInt48(rec[35..]));
        bool spillover = (rec[0] & FlagSpillover) != 0;

        PropertyValue value;
        if (spillover)
        {
            long blobId = RecordHelpers.ReadInt40(rec[30..]);
            long len = _blobs.GetLength(blobId);
            byte[] buf = new byte[len];
            _blobs.Read(blobId, buf);
            value = vtype == PropertyValueType.String
                ? PropertyValue.FromUtf8(buf)
                : PropertyValue.FromBytes(buf);
        }
        else
        {
            value = ReadInline(rec[6..], vtype);
        }

        return new PropertyReadHandle(propId, keyId, nextId, value);
    }

    public PropertyEnumerator Enumerate(PropertyId firstPropId)
        => new(this, firstPropId);

    // --- internal bulk-load helpers ---

    internal PropertyId BulkCreate(int keyId, PropertyValueType type, long scalar, byte[]? data, long nextPropId)
    {
        long id = _hwm++;
        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();

        bool spillover = type is PropertyValueType.String or PropertyValueType.Bytes
            && (data?.Length ?? 0) > InlineCapacity;

        byte flags = FlagInUse;
        if (spillover) flags |= FlagSpillover;
        rec[0] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(rec[1..], keyId);
        rec[5] = (byte)type;
        RecordHelpers.WriteInt48(rec[35..], nextPropId);

        if (spillover)
        {
            long blobId = _blobs.Write(data!);
            RecordHelpers.WriteInt40(rec[30..], blobId);
        }
        else
        {
            PropertyValue pv = type switch
            {
                PropertyValueType.Bool   => PropertyValue.FromBool(scalar != 0),
                PropertyValueType.Int32  => PropertyValue.FromInt32((int)scalar),
                PropertyValueType.Int64  => PropertyValue.FromInt64(scalar),
                PropertyValueType.Double => PropertyValue.FromDouble(BitConverter.Int64BitsToDouble(scalar)),
                PropertyValueType.String => PropertyValue.FromUtf8((data ?? Array.Empty<byte>()).AsSpan()),
                PropertyValueType.Bytes  => PropertyValue.FromBytes((data ?? Array.Empty<byte>()).AsSpan()),
                _ => throw new Quiver.Core.CorruptionException($"Unknown property type {type}")
            };
            WriteInline(rec[6..], pv);
        }

        _file.UnpinDirty(wpid, 0);
        return new PropertyId(id);
    }

    internal void BulkFlushMeta() => FlushMeta();

    // --- private ---

    private static void WriteInline(Span<byte> inline, in PropertyValue v)
    {
        switch (v.Type)
        {
            case PropertyValueType.Bool:
                inline[0] = v.BoolValue ? (byte)1 : (byte)0;
                break;
            case PropertyValueType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(inline, v.Int32Value);
                break;
            case PropertyValueType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(inline, v.Int64Value);
                break;
            case PropertyValueType.Double:
                BinaryPrimitives.WriteInt64LittleEndian(inline, BitConverter.DoubleToInt64Bits(v.DoubleValue));
                break;
            case PropertyValueType.String:
            {
                var span = v.Utf8StringValue;
                inline[0] = (byte)span.Length;
                span.CopyTo(inline[1..]);
                break;
            }
            case PropertyValueType.Bytes:
            {
                var span = v.BytesValue;
                inline[0] = (byte)span.Length;
                span.CopyTo(inline[1..]);
                break;
            }
        }
    }

    private static PropertyValue ReadInline(ReadOnlySpan<byte> inline, PropertyValueType vtype)
        => vtype switch
        {
            PropertyValueType.Bool => PropertyValue.FromBool(inline[0] != 0),
            PropertyValueType.Int32 => PropertyValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(inline)),
            PropertyValueType.Int64 => PropertyValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(inline)),
            PropertyValueType.Double => PropertyValue.FromDouble(
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(inline))),
            PropertyValueType.String => PropertyValue.FromUtf8(CopyInlineBytes(inline)),
            PropertyValueType.Bytes => PropertyValue.FromBytes(CopyInlineBytes(inline)),
            _ => throw new Quiver.Core.CorruptionException($"Unknown property value type {vtype}"),
        };

    private static byte[] CopyInlineBytes(ReadOnlySpan<byte> inline)
    {
        int len = inline[0];
        var buf = new byte[len];
        inline[1..(1 + len)].CopyTo(buf);
        return buf;
    }

    private (PageId pageId, int offset) Location(long id)
    {
        int rpp = RecordsPerPage;
        return (new PageId(id / rpp + 2), (int)(id % rpp) * RecordSize);
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.PropertyRecord);
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _freeHead = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaFreeHead..]);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
    }

    private void FlushMeta()
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        _file.UnpinDirty(HeaderPageId, 0);
    }
}
