using System.Buffers.Binary;
using System.Text;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

/// <summary>
/// vector index definition を単一の transactional catalog page に保存する。
/// derived payload や HNSW page の所有権は持たない。
/// </summary>
internal sealed class VectorDefinitionCatalog
{
    private const int OffCount = 0;
    private const int OffFamilyVersion = 31;
    private const int OffEntries = 64;
    private static readonly PageId HeaderPageId = new(1);

    private readonly IPagedFile _file;
    private readonly List<VectorIndexDescriptor> _entries = [];

    internal VectorDefinitionCatalog(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            Save();
        }
        else
        {
            CheckFamilyVersion();
            Load();
        }
    }

    internal IReadOnlyList<VectorIndexDescriptor> Entries => _entries;

    internal void Register(VectorIndexDescriptor descriptor)
    {
        if (_entries.Any(
                entry => string.Equals(entry.Name, descriptor.Name, StringComparison.Ordinal)))
        {
            throw new VectorException(
                $"Vector index '{descriptor.Name}' already exists.");
        }
        _entries.Add(descriptor);
        Save();
    }

    internal bool Unregister(string name)
    {
        int index = _entries.FindIndex(
            entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
        if (index < 0)
            return false;
        _entries.RemoveAt(index);
        Save();
        return true;
    }

    internal void Reload() => Load();

    private void Load()
    {
        _entries.Clear();
        using var page = _file.PinForRead(HeaderPageId);
        ReadOnlySpan<byte> body = page.Data;
        int count = BinaryPrimitives.ReadInt32LittleEndian(body[OffCount..]);
        int position = OffEntries;
        for (int i = 0; i < count; i++)
        {
            EnsureAvailable(body, position, sizeof(int), "entry length");
            int entryLength = BinaryPrimitives.ReadInt32LittleEndian(body[position..]);
            position += sizeof(int);
            int entryEnd;
            try
            {
                entryEnd = checked(position + entryLength);
            }
            catch (OverflowException)
            {
                throw new StorageException(
                    $"Vector definition catalog entry {i} has an invalid length.");
            }
            if (entryLength < 0 || entryEnd > body.Length)
            {
                throw new StorageException(
                    $"Vector definition catalog entry {i} exceeds the catalog page.");
            }

            string name = ReadString(body, ref position, entryEnd)!;
            EnsureAvailable(body, position, 1 + sizeof(int), "target", entryEnd);
            var ownerKind = (EntityKind)body[position++];
            int propertyKey = BinaryPrimitives.ReadInt32LittleEndian(body[position..]);
            position += sizeof(int);
            string? scope = ReadString(body, ref position, entryEnd);
            EnsureAvailable(
                body,
                position,
                sizeof(int) + 2 + sizeof(int) * 6,
                "definition",
                entryEnd);
            int dimensions = BinaryPrimitives.ReadInt32LittleEndian(body[position..]);
            position += sizeof(int);
            var metric = (DistanceMetric)body[position++];
            var elementType = (VectorElementType)body[position++];
            int hnswM = ReadInt32(body, ref position);
            int hnswMMax0 = ReadInt32(body, ref position);
            int hnswMaxLayers = ReadInt32(body, ref position);
            int hnswEfConstruction = ReadInt32(body, ref position);
            int maximumDeltaEntries = ReadInt32(body, ref position);
            int maximumSegments = ReadInt32(body, ref position);
            if (position != entryEnd)
            {
                throw new StorageException(
                    $"Vector definition catalog entry {i} has unknown trailing fields.");
            }

            var descriptor = new VectorIndexDescriptor(
                name,
                ownerKind,
                new PropertyKeyId(propertyKey),
                scope,
                dimensions,
                metric,
                elementType,
                hnswM,
                hnswMMax0,
                hnswMaxLayers,
                hnswEfConstruction,
                new VectorSegmentPolicy(maximumDeltaEntries, maximumSegments));
            VectorIndexDescriptorValidator.Validate(descriptor);
            _entries.Add(descriptor);
        }
    }

    private void Save()
    {
        using var page = _file.PinForWrite(HeaderPageId);
        Span<byte> body = page.Data;
        body[OffEntries..].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(body[OffCount..], _entries.Count);
        body[OffFamilyVersion] = StorageFormatVersion.Current;
        int position = OffEntries;
        foreach (VectorIndexDescriptor descriptor in _entries)
        {
            VectorSegmentPolicy policy = descriptor.SegmentPolicy ?? new();
            int entryLength =
                EncodedStringSize(descriptor.Name)
                + 1
                + sizeof(int)
                + EncodedStringSize(descriptor.TargetScope)
                + sizeof(int)
                + 2
                + sizeof(int) * 6;
            if (entryLength > body.Length - position - sizeof(int))
            {
                throw new StorageException(
                    $"Vector definition catalog overflow ({_entries.Count} indexes).");
            }

            BinaryPrimitives.WriteInt32LittleEndian(body[position..], entryLength);
            position += sizeof(int);
            WriteString(body, ref position, descriptor.Name);
            body[position++] = (byte)descriptor.OwnerKind;
            WriteInt32(body, ref position, descriptor.TargetPropertyKeyId.Value);
            WriteString(body, ref position, descriptor.TargetScope);
            WriteInt32(body, ref position, descriptor.Dimensions);
            body[position++] = (byte)descriptor.Metric;
            body[position++] = (byte)descriptor.ElementType;
            WriteInt32(body, ref position, descriptor.HnswM);
            WriteInt32(body, ref position, descriptor.HnswMMax0);
            WriteInt32(body, ref position, descriptor.HnswMaxLayers);
            WriteInt32(body, ref position, descriptor.HnswEfConstruction);
            WriteInt32(body, ref position, policy.MaximumDeltaEntries);
            WriteInt32(body, ref position, policy.MaximumSegments);
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> body, ref int position)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(body[position..]);
        position += sizeof(int);
        return value;
    }

    private static void WriteInt32(Span<byte> body, ref int position, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(body[position..], value);
        position += sizeof(int);
    }

    private static string? ReadString(
        ReadOnlySpan<byte> body,
        ref int position,
        int entryEnd)
    {
        EnsureAvailable(body, position, sizeof(int), "string length", entryEnd);
        int length = ReadInt32(body, ref position);
        if (length < 0)
            return null;
        EnsureAvailable(body, position, length, "string bytes", entryEnd);
        string value = Encoding.UTF8.GetString(body.Slice(position, length));
        position += length;
        return value;
    }

    private static void WriteString(
        Span<byte> body,
        ref int position,
        string? value)
    {
        if (value is null)
        {
            WriteInt32(body, ref position, -1);
            return;
        }
        int length = Encoding.UTF8.GetByteCount(value);
        WriteInt32(body, ref position, length);
        Encoding.UTF8.GetBytes(value, body[position..]);
        position += length;
    }

    private static int EncodedStringSize(string? value)
        => sizeof(int) + (value is null ? 0 : Encoding.UTF8.GetByteCount(value));

    private static void EnsureAvailable(
        ReadOnlySpan<byte> body,
        int position,
        int length,
        string field,
        int? limit = null)
    {
        int end;
        try
        {
            end = checked(position + length);
        }
        catch (OverflowException)
        {
            throw new StorageException(
                $"Vector definition catalog {field} has an invalid length.");
        }
        if (length < 0 || position < 0 || end > (limit ?? body.Length))
        {
            throw new StorageException(
                $"Vector definition catalog {field} exceeds its entry boundary.");
        }
    }

    private void CheckFamilyVersion()
    {
        using var page = _file.PinForRead(HeaderPageId);
        byte version = page.Data[OffFamilyVersion];
        if (version != StorageFormatVersion.Current)
        {
            throw new StorageFormatMismatchException(
                "vectordefinitioncatalog",
                version,
                StorageFormatVersion.Current);
        }
    }
}
