using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Wal;
using Yatagarasu.Transactions;

namespace Yatagarasu.Storage.Records;

internal sealed class PropertyVersionStore : IPropertyStore, ITransactionPropertyStore
{
    internal const int RecordSize = 84;
    internal static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize;

    private const byte FlagInUse = 0x01;
    private const byte FlagSpillover = 0x02;
    private const byte FlagVectorPayload = 0x04;
    private const int OffFlags = 0;
    private const int OffCardinality = 1;
    private const int OffValueType = 2;
    private const int OffOwner = 4;
    private const int OffKey = 12;
    private const int OffPreviousVersion = 16;
    private const int OffNextOwned = 22;
    private const int OffValueLength = 28;
    private const int OffValue = 32;
    private const int InlineCapacity = 24;
    private const int OffChecksum = 56;
    private const int OffXmin = 60;
    private const int OffXmax = 68;
    private const int OffGeneration = 76;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;
    private const int MetaHwm = 8;
    private const int MetaAllocatedHwm = 16;
    private const int MetaFormatVersion = 31;

    private readonly IPagedFile _file;
    private readonly BlobStore _blobs;
    private readonly VectorPayloadStore _vectors;
    private long _freeHead;
    private long _hwm;
    private long _allocatedHwm;
    private bool _metaDirty;

    public PropertyVersionStore(
        IPagedFile file,
        IPagedFile blobFile,
        IPagedFile vectorMetadataFile,
        IPagedFile vectorBlobFile)
    {
        _file = file;
        _blobs = new BlobStore(blobFile);
        _vectors = new VectorPayloadStore(vectorMetadataFile, vectorBlobFile);
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _freeHead = -1;
            _hwm = 0;
            _allocatedHwm = 0;
            FlushMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
        }
    }

    public PropertyVersionRef Create(
        PropertyAddress address,
        PropertyCardinality cardinality,
        in PropertyValue value,
        PropertyVersionRef currentFirst)
        => CreateCore(
            address,
            cardinality,
            in value,
            currentFirst,
            TransactionId.Bootstrap,
            LatestVisible,
            deferMetaFlush: false);

    public PropertyVersionRef Create(
        PropertyAddress address,
        PropertyCardinality cardinality,
        in PropertyValue value,
        PropertyVersionRef currentFirst,
        TransactionId transactionId,
        VersionVisible visibility)
        => CreateCore(
            address,
            cardinality,
            in value,
            currentFirst,
            transactionId,
            visibility,
            deferMetaFlush: true);

    private PropertyVersionRef CreateCore(
        PropertyAddress address,
        PropertyCardinality cardinality,
        in PropertyValue value,
        PropertyVersionRef currentFirst,
        TransactionId transactionId,
        VersionVisible visibility,
        bool deferMetaFlush)
    {
        if (!address.IsValid)
            throw new ArgumentException("有効な property owner と key が必要です。", nameof(address));
        if (cardinality is not PropertyCardinality.Single and not PropertyCardinality.Set)
            throw new ArgumentOutOfRangeException(nameof(cardinality));

        PropertyVersionRef previousVersion = FindPreviousVersion(address, currentFirst, visibility);
        long sequence;
        int generation;
        if (_freeHead >= 0)
        {
            sequence = _freeHead;
            var (freePage, freeOffset) = Location(sequence);
            using (var h = _file.PinForRead(freePage))
            {
                _freeHead = RecordHelpers.ReadInt48(h.Data[(freeOffset + OffNextOwned)..]);
                generation = checked((int)BinaryPrimitives.ReadInt64LittleEndian(
                    h.Data[(freeOffset + OffGeneration)..]));
            }
            long previousGeneration = generation;
            if (previousGeneration >= EntityRef.MaxGeneration)
                throw new InvalidOperationException("Generation が上限に達した property slot は再利用できません。");
            generation = checked((int)previousGeneration + 1);
        }
        else
        {
            sequence = _hwm++;
            // HWM は vacuum で縮むため、append 位置が過去の slot を指す場合だけ record を読む。
            // 常に読む方式は通常の単調 append ごとに read pin を増やし、単一 writer の hot path を悪化させる。
            long previousGeneration = sequence < _allocatedHwm
                ? ReadStoredGeneration(sequence)
                : 0;
            if (previousGeneration >= EntityRef.MaxGeneration)
                throw new InvalidOperationException("Generation が上限に達した property slot は再利用できません。");
            generation = checked((int)previousGeneration + 1);
            if (sequence >= _allocatedHwm)
                _allocatedHwm = sequence + 1;
        }

        var version = PropertyVersionRef.Create(sequence, generation);
        var (pageId, offset) = Location(sequence);
        EnsurePage(pageId);
        var ph = _file.PinForWrite(pageId);
        Span<byte> record = ph.Data.Slice(offset, RecordSize);
        record.Clear();
        record[OffFlags] = FlagInUse;
        record[OffCardinality] = (byte)cardinality;
        record[OffValueType] = (byte)value.Type;
        BinaryPrimitives.WriteInt64LittleEndian(
            record[OffOwner..],
            EntityRef.Pack(address.Owner.Kind, address.Owner.Sequence, address.Owner.Generation));
        BinaryPrimitives.WriteInt32LittleEndian(record[OffKey..], address.Key.Value);
        RecordHelpers.WriteInt48(record[OffPreviousVersion..], previousVersion.Sequence);
        RecordHelpers.WriteInt48(record[OffNextOwned..], currentFirst.Sequence);
        WriteValue(record, in value);
        BinaryPrimitives.WriteInt64LittleEndian(record[OffXmin..], transactionId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(record[OffXmax..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(record[OffGeneration..], generation);
        ph.Dispose();
        _metaDirty = true;
        if (!deferMetaFlush)
            FlushMeta();
        return version;
    }

    public PropertyVersionRef Delete(
        EntityRef owner,
        PropertyVersionRef version,
        PropertyVersionRef currentFirst)
        => Delete(owner, version, currentFirst, TransactionId.Bootstrap, LatestVisible);

    public PropertyVersionRef Delete(
        EntityRef owner,
        PropertyVersionRef version,
        PropertyVersionRef currentFirst,
        TransactionId transactionId,
        VersionVisible visibility)
    {
        PropertyVersionRecord record = Read(owner, version, visibility);
        if (record.InUse)
        {
            var (pageId, offset) = Location(record.Version.Sequence);
            var page = _file.PinForWrite(pageId);
            BinaryPrimitives.WriteInt64LittleEndian(
                page.Data[(offset + OffXmax)..],
                transactionId.Value);
            page.Dispose();
        }
        return currentFirst;
    }

    public PropertyVersionRecord Read(EntityRef owner, PropertyVersionRef version)
        => ReadCore(owner, version, LatestVisible, applyVisibility: true);

    public PropertyVersionRecord Read(PropertyVersionRef version)
        => ReadCore(null, version, LatestVisible, applyVisibility: true);

    public PropertyVersionRecord Read(
        EntityRef owner,
        PropertyVersionRef version,
        VersionVisible visibility)
        => ReadCore(owner, version, visibility, applyVisibility: true);

    public PropertyVersionRecord Read(
        PropertyVersionRef version,
        VersionVisible visibility)
        => ReadCore(null, version, visibility, applyVisibility: true);

    private PropertyVersionRecord ReadCore(
        EntityRef? expectedOwner,
        PropertyVersionRef version,
        VersionVisible visibility,
        bool applyVisibility)
    {
        if ((expectedOwner.HasValue && !expectedOwner.Value.IsValid)
            || !version.IsValid
            || version.Sequence >= _hwm)
            return Missing(version);

        long sequence = version.Sequence;
        var (pageId, offset) = Location(sequence);
        long generation;
        long xmin;
        long xmax;
        bool inUse;
        long packedOwner;
        int keyId;
        PropertyCardinality cardinality;
        long previousSequence;
        long nextSequence;
        PropertyValue value;
        using (var h = _file.PinForRead(pageId))
        {
            ReadOnlySpan<byte> record = h.Data.Slice(offset, RecordSize);
            generation = BinaryPrimitives.ReadInt64LittleEndian(record[OffGeneration..]);
            if (generation <= 0)
                return Missing(version);
            if (version.Generation != 0 && version.Generation != generation)
                return Missing(version);
            xmin = BinaryPrimitives.ReadInt64LittleEndian(record[OffXmin..]);
            xmax = BinaryPrimitives.ReadInt64LittleEndian(record[OffXmax..]);
            inUse = (record[OffFlags] & FlagInUse) != 0;
            packedOwner = BinaryPrimitives.ReadInt64LittleEndian(record[OffOwner..]);
            keyId = BinaryPrimitives.ReadInt32LittleEndian(record[OffKey..]);
            cardinality = (PropertyCardinality)record[OffCardinality];
            previousSequence = RecordHelpers.ReadInt48(record[OffPreviousVersion..]);
            nextSequence = RecordHelpers.ReadInt48(record[OffNextOwned..]);
            value = inUse ? ReadValue(record) : default;
        }
        var actualVersion = PropertyVersionRef.Create(sequence, checked((int)generation));
        if (!inUse)
            return Missing(actualVersion);

        EntityRef storedOwner = DecodeOwner(packedOwner);
        if (expectedOwner.HasValue && storedOwner != expectedOwner.Value)
            throw new CorruptionException(
                $"Property version {sequence} belongs to {storedOwner.Kind}:{storedOwner.Value}, not {expectedOwner.Value.Kind}:{expectedOwner.Value.Value}.");

        var address = new PropertyAddress(storedOwner, new PropertyKeyId(keyId));
        var previous = Materialize(previousSequence);
        var next = Materialize(nextSequence);
        if (applyVisibility && inUse && !visibility(xmin, xmax))
            inUse = false;
        return new PropertyVersionRecord(
            actualVersion,
            address,
            cardinality,
            value,
            previous,
            next,
            xmin,
            xmax,
            inUse);
    }

    public PropertyCursor Enumerate(EntityRef owner, PropertyVersionRef firstVersion)
        => new(this, owner, Materialize(firstVersion.Sequence));

    internal PropertyVersionRef BulkCreate(
        EntityRef owner,
        int keyId,
        PropertyCardinality cardinality,
        PropertyValueType type,
        long scalar,
        byte[]? data,
        long nextPropertySequence)
    {
        PropertyValue value = type switch
        {
            PropertyValueType.Bool => PropertyValue.FromBool(scalar != 0),
            PropertyValueType.Int32 => PropertyValue.FromInt32((int)scalar),
            PropertyValueType.Int64 => PropertyValue.FromInt64(scalar),
            PropertyValueType.Double => PropertyValue.FromDouble(BitConverter.Int64BitsToDouble(scalar)),
            PropertyValueType.String => PropertyValue.FromUtf8(data ?? []),
            PropertyValueType.Bytes => PropertyValue.FromBytes(data ?? []),
            PropertyValueType.FloatArray => PropertyValue.FromFloatArray(MemoryMarshal.Cast<byte, float>((data ?? []).AsSpan())),
            _ => throw new CorruptionException($"Unknown property type {type}"),
        };
        return CreateCore(
            new PropertyAddress(owner, new PropertyKeyId(keyId)),
            cardinality,
            in value,
            PropertyVersionRef.FromSequence(nextPropertySequence),
            TransactionId.Bootstrap,
            LatestVisible,
            deferMetaFlush: true);
    }

    internal void FlushMeta() => FlushMeta(initialise: false);

    internal void BulkFlushMeta() => FlushMeta();

    internal sealed record PropertyVacuumPlan(
        PropertyVersionRef Head,
        IReadOnlyList<PropertyVersionRef> Retained,
        IReadOnlyList<PropertyVersionRef> Reclaimed);

    internal PropertyVacuumPlan PlanVacuum(
        EntityRef owner, PropertyVersionRef head, bool ownerDead,
        long horizonTxId, CommittedTxRegistry committed)
    {
        var retained = new List<PropertyVersionRef>();
        var reclaimed = new List<PropertyVersionRef>();
        var visited = new HashSet<long>();
        PropertyVersionRef current = head;
        while (current.IsValid)
        {
            if (!visited.Add(current.Sequence))
                throw new CorruptionException("Property owner chain contains a cycle.");
            if (current.Sequence >= _hwm)
                throw new CorruptionException("Property owner chain references a missing or stale version.");
            var (pageId, offset) = Location(current.Sequence);
            using var page = _file.PinForRead(pageId);
            ReadOnlySpan<byte> record = page.Data.Slice(offset, RecordSize);
            long generation = BinaryPrimitives.ReadInt64LittleEndian(record[OffGeneration..]);
            if ((record[OffFlags] & FlagInUse) == 0 || generation <= 0
                || current.Generation != 0 && current.Generation != generation)
                throw new CorruptionException("Property owner chain references a missing or stale version.");
            if (DecodeOwner(BinaryPrimitives.ReadInt64LittleEndian(record[OffOwner..])) != owner)
                throw new CorruptionException("Property owner chain references another owner or generation.");
            long xmax = BinaryPrimitives.ReadInt64LittleEndian(record[OffXmax..]);
            bool dead = ownerDead || xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax);
            (dead ? reclaimed : retained).Add(PropertyVersionRef.Create(current.Sequence, checked((int)generation)));
            long next = RecordHelpers.ReadInt48(record[OffNextOwned..]);
            if (next < -1) throw new CorruptionException("Property owner chain has an invalid next reference.");
            current = PropertyVersionRef.FromSequence(next);
        }
        return new PropertyVacuumPlan(head, retained, reclaimed);
    }

    internal sealed class PropertyVacuumContext
    {
        internal HashSet<VectorPayloadRef>? ValidatedVectorReferences;
    }

    internal PropertyVersionRef ApplyVacuum(PropertyVacuumPlan plan, PropertyVacuumContext? context = null)
    {
        if (plan.Reclaimed.Count == 0) return plan.Head;
        var vectors = new List<VectorPayloadRef>();
        var slots = new HashSet<PropertyVersionRef>();
        foreach (var version in plan.Reclaimed)
        {
            if (!version.IsValid || !slots.Add(version) || version.Sequence >= _hwm)
                throw new CorruptionException("Property reclaim plan contains a duplicate or missing slot.");
            var (pageId, offset) = Location(version.Sequence);
            using var page = _file.PinForRead(pageId);
            ReadOnlySpan<byte> record = page.Data.Slice(offset, RecordSize);
            if ((record[OffFlags] & FlagInUse) == 0
                || BinaryPrimitives.ReadInt64LittleEndian(record[OffGeneration..]) != version.Generation)
                throw new CorruptionException("Property reclaim plan references a missing or stale slot.");
            if ((record[OffFlags] & FlagVectorPayload) != 0) vectors.Add(ReadVectorReference(record));
        }
        if (vectors.Count > 0)
        {
            context ??= new PropertyVacuumContext();
            context.ValidatedVectorReferences ??= ValidateExclusiveVectorReferences();
            foreach (var vector in vectors)
                if (!context.ValidatedVectorReferences.Contains(vector))
                    throw new CorruptionException("Property reclaim plan references an unvalidated vector payload.");
        }
        // 候補列挙を完了してから変更する。途中で所有者の不一致や循環を検出しても部分回収しない。
        foreach (var version in plan.Reclaimed) ReclaimSlot(version);
        PropertyVersionRef newHead = PropertyVersionRef.Invalid;
        for (int i = plan.Retained.Count - 1; i >= 0; i--)
        {
            RewriteNext(plan.Retained[i], newHead);
            newHead = plan.Retained[i];
        }
        return newHead;
    }

    private HashSet<VectorPayloadRef> ValidateExclusiveVectorReferences()
    {
        var references = new HashSet<VectorPayloadRef>();
        for (long sequence = 0; sequence < _hwm; sequence++)
        {
            var (pageId, offset) = Location(sequence);
            using var page = _file.PinForRead(pageId);
            ReadOnlySpan<byte> record = page.Data.Slice(offset, RecordSize);
            if ((record[OffFlags] & (FlagInUse | FlagVectorPayload)) != (FlagInUse | FlagVectorPayload)) continue;
            var reference = ReadVectorReference(record);
            if (!reference.IsValid || !references.Add(reference))
                throw new CorruptionException("Vector payload reference is invalid or shared by multiple property versions.");
        }
        return references;
    }

    private static VectorPayloadRef ReadVectorReference(ReadOnlySpan<byte> record) => new(
        BinaryPrimitives.ReadInt64LittleEndian(record[OffValue..]),
        BinaryPrimitives.ReadInt32LittleEndian(record[(OffValue + sizeof(long))..]));

    internal void FinishExternalReclaim()
    {
        ShrinkHwmFromTrailingFreeSlots();
        FlushMeta();
    }

    internal long Hwm => _hwm;
    internal long FreeHead => _freeHead;
    // free slot の Generation は record に残すため、reader horizon に基づく payload GC が入るまで
    // 末尾 page を物理 truncate しない。HWM は縮めても stale ref rejection の履歴を保持する。
    internal long ComputeRequiredPageCount() => _file.PageCount;
    internal IPagedFile UnderlyingFile => _file;

    internal void ReloadMeta()
    {
        LoadMeta();
        _blobs.ReloadMeta();
        _vectors.ReloadMeta();
    }

    internal IReadOnlyList<VectorPayloadRef> ScanOrphanVectorPayloads()
    {
        var reachable = new HashSet<VectorPayloadRef>();
        for (long sequence = 0; sequence < _hwm; sequence++)
        {
            var (pageId, offset) = Location(sequence);
            using var page = _file.PinForRead(pageId);
            ReadOnlySpan<byte> record = page.Data.Slice(offset, RecordSize);
            if ((record[OffFlags] & (FlagInUse | FlagVectorPayload))
                != (FlagInUse | FlagVectorPayload))
                continue;
            reachable.Add(new VectorPayloadRef(
                BinaryPrimitives.ReadInt64LittleEndian(record[OffValue..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[(OffValue + sizeof(long))..])));
        }
        return _vectors.ScanOrphans(reachable).ToArray();
    }

    private PropertyVersionRef FindPreviousVersion(
        PropertyAddress address,
        PropertyVersionRef currentFirst,
        VersionVisible visibility)
    {
        PropertyVersionRef current = Materialize(currentFirst.Sequence);
        long guard = _hwm + 1;
        while (current.IsValid && guard-- > 0)
        {
            PropertyVersionRecord record = Read(address.Owner, current, visibility);
            if (record.Address.Key == address.Key)
                return record.Version;
            current = record.NextOwnedProperty;
        }
        return PropertyVersionRef.Invalid;
    }

    private static bool LatestVisible(long xmin, long xmax) => xmin != 0 && xmax == 0;

    private PropertyVersionRef ReclaimSlot(PropertyVersionRef version)
    {
        var (pageId, offset) = Location(version.Sequence);
        PropertyVersionRef next;
        long blobId = -1;
        VectorPayloadRef vector = VectorPayloadRef.Invalid;
        long generation;
        long nextSequence;
        using (var h = _file.PinForRead(pageId))
        {
            ReadOnlySpan<byte> record = h.Data.Slice(offset, RecordSize);
            nextSequence = RecordHelpers.ReadInt48(record[OffNextOwned..]);
            generation = BinaryPrimitives.ReadInt64LittleEndian(record[OffGeneration..]);
            if ((record[OffFlags] & FlagVectorPayload) != 0) vector = ReadVectorReference(record);
            if ((record[OffFlags] & (FlagSpillover | FlagVectorPayload)) == FlagSpillover)
                blobId = BinaryPrimitives.ReadInt64LittleEndian(record[OffValue..]);
        }
        next = Materialize(nextSequence);
        if (blobId >= 0)
            _blobs.Free(blobId);
        if (vector.IsValid && !_vectors.Free(vector))
            throw new CorruptionException("Property references an already freed vector payload.");

        var ph = _file.PinForWrite(pageId);
        Span<byte> writable = ph.Data.Slice(offset, RecordSize);
        writable.Clear();
        RecordHelpers.WriteInt48(writable[OffNextOwned..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(writable[OffGeneration..], generation);
        ph.Dispose();
        _freeHead = version.Sequence;
        _metaDirty = true;
        return next;
    }

    private void RewriteNext(PropertyVersionRef version, PropertyVersionRef next)
    {
        var (pageId, offset) = Location(version.Sequence);
        var ph = _file.PinForWrite(pageId);
        RecordHelpers.WriteInt48(ph.Data[(offset + OffNextOwned)..], next.Sequence);
        ph.Dispose();
    }

    private void ShrinkHwmFromTrailingFreeSlots()
    {
        while (_hwm > 0)
        {
            var (pageId, offset) = Location(_hwm - 1);
            using var h = _file.PinForRead(pageId);
            if ((h.Data[offset + OffFlags] & FlagInUse) != 0)
                break;
            _hwm--;
        }
        RebuildFreeList();
    }

    private void RebuildFreeList()
    {
        _freeHead = -1;
        for (long sequence = 0; sequence < _hwm; sequence++)
        {
            var (pageId, offset) = Location(sequence);
            bool inUse;
            using (var h = _file.PinForRead(pageId))
                inUse = (h.Data[offset + OffFlags] & FlagInUse) != 0;
            if (inUse)
                continue;

            var ph = _file.PinForWrite(pageId);
            RecordHelpers.WriteInt48(ph.Data[(offset + OffNextOwned)..], _freeHead);
            ph.Dispose();
            _freeHead = sequence;
        }
    }

    private void WriteValue(Span<byte> record, in PropertyValue value)
    {
        Span<byte> scalar = stackalloc byte[8];
        switch (value.Type)
        {
            case PropertyValueType.Bool:
                scalar[0] = value.BoolValue ? (byte)1 : (byte)0;
                WriteEncodedValue(record, scalar[..1]);
                return;
            case PropertyValueType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(scalar, value.Int32Value);
                WriteEncodedValue(record, scalar[..4]);
                return;
            case PropertyValueType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(scalar, value.Int64Value);
                WriteEncodedValue(record, scalar);
                return;
            case PropertyValueType.Double:
                BinaryPrimitives.WriteInt64LittleEndian(scalar, BitConverter.DoubleToInt64Bits(value.DoubleValue));
                WriteEncodedValue(record, scalar);
                return;
            case PropertyValueType.String:
                WriteEncodedValue(record, value.Utf8StringValue);
                return;
            case PropertyValueType.Bytes:
                WriteEncodedValue(record, value.BytesValue);
                return;
            case PropertyValueType.FloatArray:
                VectorPayloadRef reference = _vectors.Write(value.FloatArrayValue);
                record[OffFlags] |= FlagVectorPayload;
                BinaryPrimitives.WriteInt32LittleEndian(
                    record[OffValueLength..], checked(value.FloatArrayValue.Length * sizeof(float)));
                BinaryPrimitives.WriteInt64LittleEndian(record[OffValue..], reference.Sequence);
                BinaryPrimitives.WriteInt32LittleEndian(
                    record[(OffValue + sizeof(long))..], reference.Generation);
                return;
            default:
                throw new CorruptionException($"Unknown property value type {value.Type}");
        }
    }

    private void WriteEncodedValue(Span<byte> record, scoped ReadOnlySpan<byte> bytes)
    {
        BinaryPrimitives.WriteInt32LittleEndian(record[OffValueLength..], bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[OffChecksum..], Crc32.HashToUInt32(bytes));
        if (bytes.Length <= InlineCapacity)
        {
            bytes.CopyTo(record[OffValue..(OffValue + InlineCapacity)]);
            return;
        }

        record[OffFlags] |= FlagSpillover;
        long blobId = _blobs.Write(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(record[OffValue..], blobId);
    }

    private PropertyValue ReadValue(ReadOnlySpan<byte> record)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(record[OffValueLength..]);
        if (length < 0)
            throw new CorruptionException("Property value length is negative.");
        if ((record[OffFlags] & FlagVectorPayload) != 0)
        {
            if ((PropertyValueType)record[OffValueType] != PropertyValueType.FloatArray)
                throw new CorruptionException("Only FloatArray properties may reference vector payloads.");
            var reference = new VectorPayloadRef(
                BinaryPrimitives.ReadInt64LittleEndian(record[OffValue..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[(OffValue + sizeof(long))..]));
            float[] elements = _vectors.Read(reference);
            if (length != checked(elements.Length * sizeof(float)))
                throw new CorruptionException("Property vector length does not match its payload reference.");
            return PropertyValue.FromFloatArray(elements);
        }
        byte[]? allocated = null;
        ReadOnlySpan<byte> bytes;
        if ((record[OffFlags] & FlagSpillover) != 0)
        {
            long blobId = BinaryPrimitives.ReadInt64LittleEndian(record[OffValue..]);
            if (_blobs.GetLength(blobId) != length)
                throw new CorruptionException("Property blob length does not match its durable reference.");
            allocated = new byte[length];
            if (_blobs.Read(blobId, allocated) != length)
                throw new CorruptionException("Property blob is truncated.");
            bytes = allocated;
        }
        else
        {
            if (length > InlineCapacity)
                throw new CorruptionException("Inline property length exceeds the record capacity.");
            bytes = record.Slice(OffValue, length);
        }

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(record[OffChecksum..]);
        if (Crc32.HashToUInt32(bytes) != expected)
            throw new CorruptionException("Property payload checksum mismatch.");

        return (PropertyValueType)record[OffValueType] switch
        {
            PropertyValueType.Bool when length == 1 => PropertyValue.FromBool(bytes[0] != 0),
            PropertyValueType.Int32 when length == 4 => PropertyValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            PropertyValueType.Int64 when length == 8 => PropertyValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
            PropertyValueType.Double when length == 8 => PropertyValue.FromDouble(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))),
            PropertyValueType.String => PropertyValue.FromUtf8(allocated ?? bytes.ToArray()),
            PropertyValueType.Bytes => PropertyValue.FromBytes(allocated ?? bytes.ToArray()),
            PropertyValueType.FloatArray when length % sizeof(float) == 0 => PropertyValue.FromFloatArray(MemoryMarshal.Cast<byte, float>((allocated ?? bytes.ToArray()).AsSpan())),
            var type => throw new CorruptionException($"Invalid property value encoding for {type}.")
        };
    }

    private PropertyVersionRef Materialize(long sequence)
    {
        if (sequence < 0 || sequence >= _hwm)
            return PropertyVersionRef.Invalid;
        long generation = ReadStoredGeneration(sequence);
        return generation <= 0
            ? PropertyVersionRef.Invalid
            : PropertyVersionRef.Create(sequence, checked((int)generation));
    }

    private long ReadStoredGeneration(long sequence)
    {
        var (pageId, offset) = Location(sequence);
        if (pageId.Value >= _file.PageCount)
            return 0;
        using var page = _file.PinForRead(pageId);
        return BinaryPrimitives.ReadInt64LittleEndian(page.Data[(offset + OffGeneration)..]);
    }

    private static EntityRef DecodeOwner(long packed)
        => EntityRef.Create(
            EntityRef.UnpackKind(packed),
            EntityRef.UnpackSequence(packed),
            EntityRef.UnpackGeneration(packed));

    private static PropertyVersionRecord Missing(PropertyVersionRef version)
        => new(
            version,
            default,
            default,
            default,
            PropertyVersionRef.Invalid,
            PropertyVersionRef.Invalid,
            xmin: 0,
            xmax: 0,
            inUse: false);

    private (PageId PageId, int Offset) Location(long sequence)
        => (new PageId(sequence / RecordsPerPage + 2), (int)(sequence % RecordsPerPage) * RecordSize);

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
        _allocatedHwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaAllocatedHwm..]);
        _metaDirty = false;
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte version = h.Data[MetaFormatVersion];
        if (version != StorageFormatVersion.Current)
            throw new StorageFormatMismatchException("property-versions", version, StorageFormatVersion.Current);
    }

    private void FlushMeta(bool initialise = false)
    {
        if (!initialise && !_metaDirty)
            return;
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaAllocatedHwm..], _allocatedHwm);
        if (initialise)
            ph.Data[MetaFormatVersion] = StorageFormatVersion.Current;
        ph.Dispose();
        _metaDirty = false;
    }
}
