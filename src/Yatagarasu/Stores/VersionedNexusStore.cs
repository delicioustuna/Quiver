using System.Buffers.Binary;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

/// <summary>vacuum: 可視性フィルタを通さない raw な header 情報 (heap head 由来)。</summary>
internal struct RawNexusHeader
{
    public bool InUse;
    public IncidenceId FirstIncidence;
    public PropertyVersionRef FirstProperty;
    public long Xmin;
    public long Xmax;
}

/// <summary>
/// <see cref="VersionedRecordHeap"/> + <see cref="ItemPointerMap"/> 上に実装した
/// MVCC 対応 nexus store。header が nexus の可視性の正本になる。
///
/// <para>header payload (15B, version ヘッダ 24B の後ろ):</para>
/// <code>
///   [0] flags          : u8    (FlagInUse)
///   [1] type           : i16
///   [3] firstIncidence : Int48 (Sequence)
///   [9] firstProp      : Int48 (Sequence)
/// </code>
///
/// <para>xmin/xmax は heap の version ヘッダ、generation は
/// <see cref="IEntityVersionStore"/> sidecar で管理する。sequence は free list から
/// 再利用し、stale 参照は世代照合で弾く (vertex store と同セマンティクス)。</para>
/// </summary>
internal sealed class VersionedNexusStore : INexusStore, ITransactionNexusStore
{
    // NexusWriteHandle が in-place 更新する固定フィールド領域。
    private const int PayloadSize = 15;
    private const int OffFlags = 0;
    private const int OffType = 1;
    private const int OffFirstIncidence = 3;
    private const int OffFirstProperty = 9;
    private const byte FlagInUse = 0x01;
    private static readonly int HdrSize = VersionedRecordHeap.VersionHeaderSize;

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private readonly VersionedRecordHeap _heap;
    private readonly IEntityVersionStore _versions;
    private long _inUseCount;

    public VersionedNexusStore(
        IPagedFile heapFile,
        ItemPointerMap map,
        IEntityVersionStore? versions = null)
    {
        _file = heapFile;
        _map = map;
        _heap = new VersionedRecordHeap(heapFile, map);
        _versions = versions ?? new InMemoryEntityVersionStore();
        _inUseCount = RecomputeInUse();
    }

    public long InUseCount => _inUseCount;
    public long SequenceHighWaterMark => _map.Hwm;

    public int CurrentGeneration(long sequence)
    {
        if (sequence < 0 || sequence >= _map.Hwm)
            return -1;
        long generation = _versions.Read(sequence).Generation;
        return generation <= 0 ? -1 : checked((int)generation);
    }

    public NexusId Create(
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore incidenceStore,
        IVertexIncidenceHeadStore vertexHeads)
        => Create(type, members, incidenceStore, vertexHeads, TransactionId.Bootstrap);

    public NexusId Create(
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore incidenceStore,
        IVertexIncidenceHeadStore vertexHeads,
        TransactionId transactionId)
    {
        // 検証はどのレコードよりも先。失敗時に header・incidence・vertex head の
        // いずれにも書き込みを残さない契約 (テストで担保)。
        Validate(type, members);

        long sequence = NextSequence();
        long generation = _versions.Read(sequence).Generation + 1;
        var nexusId = NexusId.Create(sequence, checked((int)generation));

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload.Clear();
        payload[OffFlags] = FlagInUse;
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffType..], checked((short)type.Value));
        RecordHelpers.WriteInt48(payload[OffFirstIncidence..], IncidenceId.Invalid.Sequence);
        RecordHelpers.WriteInt48(payload[OffFirstProperty..], PropertyVersionRef.Invalid.Sequence);

        _heap.Insert(sequence, payload, transactionId.Value);
        _versions.Write(sequence, new EntityVersionMeta(
            transactionId.Value, 0, generation));
        _inUseCount++;

        IncidenceId first = IncidenceId.Invalid;
        IncidenceId previousInNexus = IncidenceId.Invalid;

        for (int index = 0; index < members.Length; index++)
        {
            IncidenceMember member = members[index];
            // vertex chain は head insert: 新 incidence を先頭に置き、その nextInVertex を
            // 旧 head に向ける。逆リンクは持たないので旧 head 側の書き換えは無い
            // (unlink は vacuum の chain sweep で行う)。nexus chain は作成順に末尾へ伸ばす。
            IncidenceId oldVertexHead = vertexHeads.Get(member.VertexId);
            IncidenceId current = incidenceStore.Allocate(
                nexusId,
                member.VertexId,
                member.RoleId,
                oldVertexHead,
                IncidenceId.Invalid);

            vertexHeads.Set(member.VertexId, current);

            if (previousInNexus.IsValid)
            {
                var previous = incidenceStore.Write(previousInNexus);
                previous.NextInNexus = current;
                previous.Dispose();
            }
            else
            {
                first = current;
            }

            previousInNexus = current;
        }

        var header = Write(nexusId);
        header.FirstIncidenceId = first;
        header.Dispose();
        return nexusId;
    }

    public void Delete(NexusId nexusId)
        => Delete(nexusId, TransactionId.Bootstrap);

    public void Delete(NexusId nexusId, TransactionId transactionId)
    {
        long sequence = nexusId.Sequence;
        if (sequence < 0 || sequence >= _map.Hwm) return;
        int carriedGeneration = nexusId.Generation;
        if (carriedGeneration != 0 && carriedGeneration != CurrentGeneration(sequence)) return;
        // xmax != 0 は論理削除済み。incidence には触れず header だけをスタンプする
        // (incidence の可視性は header 経由で消えるため)。
        if (!_heap.TryReadHeadRaw(sequence, out _, out _, out long xmax) || xmax != 0)
            return;

        _heap.StampXmax(sequence, transactionId.Value);
        _inUseCount--;
    }

    public NexusReadHandle Read(NexusId nexusId)
        => Read(nexusId, LatestVisible);

    public NexusReadHandle Read(NexusId nexusId, VersionVisible visibility)
    {
        long sequence = nexusId.Sequence;
        if (sequence < 0 || sequence >= _map.Hwm)
            return NotInUse(nexusId);

        // Generation 0 は「世代照合なし」の sequence 直指定。それ以外は sidecar の
        // 現世代と一致しない stale 参照 (slot 再利用後の旧 ID) を弾く。
        EntityVersionMeta version = _versions.Read(sequence);
        if (nexusId.Generation != 0 && nexusId.Generation != version.Generation)
            return NotInUse(nexusId);

        Span<byte> payload = stackalloc byte[PayloadSize];
        int length = _heap.TryReadHeadInto(
            sequence, payload, out long xmin, out long xmax, out bool hasOlderVersion);
        if (length < PayloadSize)
            return NotInUse(nexusId);

        // head 版が不可視でも、旧版が snapshot から可視なら生存として扱う
        // (削除は xmax スタンプのみで版を積まないため、payload は head と同一)。
        bool inUse = (payload[OffFlags] & FlagInUse) != 0
            && (visibility(xmin, xmax)
                || (hasOlderVersion
                    && _heap.TryReadVisible(sequence, visibility, out _, out _, out _)));

        NexusId resolvedId = NexusId.Create(sequence, checked((int)version.Generation));
        return new NexusReadHandle(
            resolvedId,
            inUse,
            new NexusTypeId(BinaryPrimitives.ReadInt16LittleEndian(payload[OffType..])),
            new IncidenceId(RecordHelpers.ReadInt48(payload[OffFirstIncidence..])),
            new PropertyVersionRef(RecordHelpers.ReadInt48(payload[OffFirstProperty..])),
            xmin,
            xmax);
    }

    public NexusWriteHandle Write(NexusId nexusId)
    {
        long sequence = nexusId.Sequence;
        int carriedGeneration = nexusId.Generation;
        if (sequence < 0
            || sequence >= _map.Hwm
            || (carriedGeneration != 0 && carriedGeneration != CurrentGeneration(sequence)))
        {
            throw new CorruptionException(
                $"Write on missing or stale nexus seq={sequence} generation={carriedGeneration}.");
        }
        ItemPointer pointer = _heap.GetHead(sequence);
        if (pointer.IsNull)
            throw new CorruptionException(
                $"Write on missing nexus seq={sequence}.");

        var pageId = new PageId(pointer.PageId);
        var page = _file.PinForWrite(pageId);
        try
        {
            var slottedPage = new SlottedPage(page.Data);
            if (!slottedPage.TryGetMutable(pointer.Slot, out Span<byte> record))
                throw new CorruptionException(
                    $"Missing version slot for nexus seq={sequence}.");
            Span<byte> fields = record.Slice(HdrSize, PayloadSize);
            return new NexusWriteHandle(page.Transfer(), fields);
        }
        catch
        {
            page.ReleaseUnchanged();
            throw;
        }
    }

    public IEnumerable<NexusId> Scan()
        => Scan(LatestVisible);

    public IEnumerable<NexusId> Scan(VersionVisible visibility)
    {
        for (long sequence = 0; sequence < _map.Hwm; sequence++)
        {
            if (!_heap.TryReadVisible(sequence, visibility, out _, out _, out _))
                continue;

            int generation = checked((int)_versions.Read(sequence).Generation);
            yield return NexusId.Create(sequence, generation);
        }
    }

    public PropertyCursor EnumerateProperties(NexusId nexusId, IPropertyStore overflowStore)
    {
        if (!_heap.TryReadVisible(nexusId.Sequence, LatestVisible, out var payload, out _, out _))
            return new PropertyCursor(overflowStore, default, PropertyVersionRef.Invalid);
        var firstProp = new PropertyVersionRef(RecordHelpers.ReadInt48(payload.AsSpan(OffFirstProperty)));
        var ownerId = nexusId.Generation == 0
            ? NexusId.Create(nexusId.Sequence, CurrentGeneration(nexusId.Sequence))
            : nexusId;
        return overflowStore.Enumerate(EntityRef.From(ownerId), firstProp);
    }

    private static bool LatestVisible(long xmin, long xmax) => xmin != 0 && xmax == 0;

    private long NextSequence()
    {
        // 回収済み sequence を free list から再利用する。世代が上限に達した sequence は
        // 永久退役 (ABA 回避)。free list が空なら高水位から新規採番。
        while (true)
        {
            long sequence = _map.PopFreeSeq();
            if (sequence < 0)
                return _map.Hwm;
            if (_versions.Read(sequence).Generation < EntityRef.MaxGeneration)
                return sequence;
        }
    }

    internal void ReloadMeta()
    {
        _map.ReloadMeta();
        _heap.ReloadMeta();
        _inUseCount = RecomputeInUse();
    }

    public bool TryReadRawHeader(long sequence, out RawNexusHeader header)
    {
        header = default;
        if (sequence < 0 || sequence >= _map.Hwm)
            return false;
        if (!_heap.TryReadHeadRaw(sequence, out var payload, out long xmin, out long xmax))
            return false;

        var span = payload.AsSpan();
        header = new RawNexusHeader
        {
            InUse = (span[OffFlags] & FlagInUse) != 0,
            FirstIncidence = new IncidenceId(RecordHelpers.ReadInt48(span[OffFirstIncidence..])),
            FirstProperty = new PropertyVersionRef(RecordHelpers.ReadInt48(span[OffFirstProperty..])),
            Xmin = xmin,
            Xmax = xmax,
        };
        return true;
    }

    /// <summary>
    /// 読み取り側の参照可能範囲を外れたヘッダの旧版を回収する。プロパティ版の回収とは独立している。
    /// </summary>
    internal void PruneDeadHeaderVersions(long sequence, long horizonTxId, CommittedTxRegistry committed)
        => _heap.PruneDeadVersions(sequence,
            (_, vx) => vx != 0 && vx < horizonTxId && committed.IsCommitted(vx));

    /// <summary>
    /// vacuum: dead header を heap から物理回収し、sequence を free list へ返す。世代は sidecar に
    /// 残るため、再利用時に <see cref="NextSequence"/> が +1 して stale ID を弾く (vertex / edge と同じ
    /// 世代照合セマンティクス)。<see cref="InUseCount"/> は削除時に減算済みなので触らない。
    /// </summary>
    internal void ReclaimHeader(long sequence)
    {
        _heap.Remove(sequence);
        _map.PushFreeSeq(sequence);
    }

    private long RecomputeInUse()
    {
        long count = 0;
        for (long sequence = 0; sequence < _map.Hwm; sequence++)
        {
            if (_heap.TryReadVisible(sequence, LatestVisible, out _, out _, out _))
                count++;
        }
        return count;
    }

    private static void Validate(
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members)
    {
        if (!type.IsValid)
            throw new ArgumentOutOfRangeException(nameof(type));
        if (type.Value > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(type));
        if (members.Length < 2)
            throw new ArgumentException("Nexus arity must be at least two.", nameof(members));

        for (int index = 0; index < members.Length; index++)
        {
            IncidenceMember member = members[index];
            if (!member.VertexId.IsValid)
                throw new ArgumentException("A member has an invalid vertex ID.", nameof(members));
            if (!member.RoleId.IsValid || member.RoleId.Value > short.MaxValue)
                throw new ArgumentException("A member has an invalid role ID.", nameof(members));

            for (int other = 0; other < index; other++)
            {
                if (members[other] == member)
                    throw new ArgumentException(
                        "The same role and vertex pair cannot occur twice.", nameof(members));
            }
        }
    }

    private static NexusReadHandle NotInUse(NexusId id)
        => new(
            id,
            false,
            NexusTypeId.Invalid,
            IncidenceId.Invalid,
            PropertyVersionRef.Invalid,
            0,
            0);
}
