using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// <see cref="VersionedRecordHeap"/> + <see cref="ItemPointerMap"/> 上に実装した
/// MVCC 対応 hyperedge store。header が hyperedge の可視性の正本になる。
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
/// 再利用し、stale 参照は世代照合で弾く (node store と同セマンティクス)。</para>
/// </summary>
internal sealed class VersionedHyperedgeStore : IHyperedgeStore
{
    // HyperedgeWriteHandle が in-place 更新する固定フィールド領域。
    private const int PayloadSize = 15;
    private const int OffFlags = 0;
    private const int OffType = 1;
    private const int OffFirstIncidence = 3;
    private const int OffFirstProperty = 9;
    private const byte FlagInUse = 0x01;
    // alloc-free な inline property 読み取りで使う stackalloc 量 (node store と同値)。
    // 超過した payload は割り当て版へフォールバックする。
    private const int InlineReadBuffer = 256;

    private static readonly int HdrSize = VersionedRecordHeap.VersionHeaderSize;

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private readonly VersionedRecordHeap _heap;
    private readonly IEntityVersionStore _versions;
    private long _inUseCount;

    public VersionedHyperedgeStore(
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

    public HyperedgeId Create(
        HyperedgeTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore incidenceStore,
        INodeIncidenceHeadStore nodeHeads)
    {
        // 検証はどのレコードよりも先。失敗時に header・incidence・node head の
        // いずれにも書き込みを残さない契約 (テストで担保)。
        Validate(type, members);

        long sequence = NextSequence();
        long generation = _versions.Read(sequence).Generation + 1;
        var hyperedgeId = HyperedgeId.Create(sequence, checked((int)generation));

        // 固定領域 + inline property 領域 (件数 0 で開始)。node / rel store と同じ
        // 可変長 payload 形式にして inline property の copy-on-write を土台にする。
        Span<byte> payload = stackalloc byte[InlinePropertyCodec.BaseSize(PayloadSize)];
        payload.Clear();
        payload[OffFlags] = FlagInUse;
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffType..], checked((short)type.Value));
        RecordHelpers.WriteInt48(payload[OffFirstIncidence..], IncidenceId.Invalid.Sequence);
        RecordHelpers.WriteInt48(payload[OffFirstProperty..], PropertyId.Invalid.Sequence);
        payload[InlinePropertyCodec.OffInlineCount(PayloadSize)] = 0;

        _heap.Insert(sequence, payload, MvccContext.CurrentTxId.Value);
        _versions.Write(sequence, new EntityVersionMeta(
            MvccContext.CurrentTxId.Value, 0, 0, long.MaxValue, generation));
        _inUseCount++;

        IncidenceId first = IncidenceId.Invalid;
        IncidenceId previousInHyperedge = IncidenceId.Invalid;

        for (int index = 0; index < members.Length; index++)
        {
            IncidenceMember member = members[index];
            // node chain は head insert: 新 incidence を先頭に置き、旧 head の
            // PreviousInNode を差し替える。hyperedge chain は作成順に末尾へ伸ばす。
            IncidenceId oldNodeHead = nodeHeads.Get(member.NodeId);
            IncidenceId current = incidenceStore.Allocate(
                hyperedgeId,
                member.NodeId,
                member.RoleId,
                IncidenceId.Invalid,
                oldNodeHead,
                IncidenceId.Invalid);

            if (oldNodeHead.IsValid)
            {
                var oldHead = incidenceStore.Write(oldNodeHead);
                oldHead.PreviousInNode = current;
                oldHead.Dispose();
            }

            nodeHeads.Set(member.NodeId, current);

            if (previousInHyperedge.IsValid)
            {
                var previous = incidenceStore.Write(previousInHyperedge);
                previous.NextInHyperedge = current;
                previous.Dispose();
            }
            else
            {
                first = current;
            }

            previousInHyperedge = current;
        }

        var header = Write(hyperedgeId);
        header.FirstIncidenceId = first;
        header.Dispose();
        return hyperedgeId;
    }

    public void Delete(HyperedgeId hyperedgeId)
    {
        long sequence = hyperedgeId.Sequence;
        // xmax != 0 は論理削除済み。incidence には触れず header だけをスタンプする
        // (incidence の可視性は header 経由で消えるため)。
        if (!_heap.TryReadHeadRaw(sequence, out _, out _, out long xmax) || xmax != 0)
            return;

        _heap.StampXmax(sequence, MvccContext.CurrentTxId.Value);
        _inUseCount--;
    }

    public HyperedgeReadHandle Read(HyperedgeId hyperedgeId)
    {
        long sequence = hyperedgeId.Sequence;
        if (sequence < 0 || sequence >= _map.Hwm)
            return NotInUse(hyperedgeId);

        // Generation 0 は「世代照合なし」の sequence 直指定。それ以外は sidecar の
        // 現世代と一致しない stale 参照 (slot 再利用後の旧 ID) を弾く。
        EntityVersionMeta version = _versions.Read(sequence);
        if (hyperedgeId.Generation != 0 && hyperedgeId.Generation != version.Generation)
            return NotInUse(hyperedgeId);

        Span<byte> payload = stackalloc byte[PayloadSize];
        int length = _heap.TryReadHeadInto(
            sequence, payload, out long xmin, out long xmax, out bool hasOlderVersion);
        if (length < PayloadSize)
            return NotInUse(hyperedgeId);

        // head 版が不可視でも、旧版が snapshot から可視なら生存として扱う
        // (削除は xmax スタンプのみで版を積まないため、payload は head と同一)。
        bool inUse = (payload[OffFlags] & FlagInUse) != 0
            && (Visibility.IsVisibleAmbient(xmin, xmax)
                || (hasOlderVersion
                    && _heap.TryReadVisible(sequence, Visibility.IsVisibleAmbient, out _, out _, out _)));

        HyperedgeId resolvedId = HyperedgeId.Create(sequence, checked((int)version.Generation));
        if (inUse)
            MvccContext.RecordRead(EntityKind.Hyperedge, sequence);

        return new HyperedgeReadHandle(
            resolvedId,
            inUse,
            new HyperedgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(payload[OffType..])),
            new IncidenceId(RecordHelpers.ReadInt48(payload[OffFirstIncidence..])),
            new PropertyId(RecordHelpers.ReadInt48(payload[OffFirstProperty..])),
            xmin,
            xmax);
    }

    public HyperedgeWriteHandle Write(HyperedgeId hyperedgeId)
    {
        ItemPointer pointer = _heap.GetHead(hyperedgeId.Sequence);
        if (pointer.IsNull)
            throw new CorruptionException(
                $"Write on missing hyperedge seq={hyperedgeId.Sequence}.");

        var pageId = new PageId(pointer.PageId);
        var page = _file.PinForWrite(pageId);
        var slottedPage = new SlottedPage(page.Data);
        if (!slottedPage.TryGetMutable(pointer.Slot, out Span<byte> record))
        {
            _file.Unpin(pageId);
            throw new CorruptionException(
                $"Missing version slot for hyperedge seq={hyperedgeId.Sequence}.");
        }

        return new HyperedgeWriteHandle(
            _file,
            pageId,
            record.Slice(HdrSize, PayloadSize));
    }

    public IEnumerable<HyperedgeId> Scan()
    {
        for (long sequence = 0; sequence < _map.Hwm; sequence++)
        {
            if (!_heap.TryReadVisible(
                    sequence, Visibility.IsVisibleAmbient, out _, out _, out _))
                continue;

            MvccContext.RecordRead(EntityKind.Hyperedge, sequence);
            int generation = checked((int)_versions.Read(sequence).Generation);
            yield return HyperedgeId.Create(sequence, generation);
        }
    }

    // ===== inline property storage (hyperedge 粒度 copy-on-write) =====
    // node / rel store と同型。header の可視性に従い、書き込みは copy-on-write で
    // 新 header 版を積む。overflow チェーンは header の FirstPropertyId から辿る
    // (呼び出し側が Read / Write ハンドル経由で管理する)。

    public bool TryGetInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId, out PropertyValue value)
    {
        value = default;
        long sequence = hyperedgeId.Sequence;
        // 可視版 payload を stackalloc へコピーして scan する。scalar は値コピーで安全、
        // String/Bytes のみ安定 byte[] へ写す。超過は割り当て版へフォールバック。
        Span<byte> buffer = stackalloc byte[InlineReadBuffer];
        int length = _heap.TryReadVisibleInto(sequence, AmbientVisible, buffer, out _, out _);
        if (length == 0) return false;
        // property read も header の read。可視版を観測したので SSN read-set に記録する。
        MvccContext.RecordRead(EntityKind.Hyperedge, sequence);
        if (length <= buffer.Length)
        {
            if (!InlinePropertyCodec.TryScan(buffer[..length], PayloadSize, keyId.Value, out var type, out var span))
                return false;
            value = InlinePropertyCodec.IsScalar(type)
                ? InlinePropertyCodec.DecodeScalar(type, span)
                : InlinePropertyCodec.Decode(type, span.ToArray());
            return true;
        }
        if (!_heap.TryReadVisible(sequence, AmbientVisible, out var payload, out _, out _)) return false;
        if (!InlinePropertyCodec.TryScan(payload, PayloadSize, keyId.Value, out var t2, out var s2)) return false;
        value = InlinePropertyCodec.Decode(t2, s2);
        return true;
    }

    public bool HasInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId)
    {
        long sequence = hyperedgeId.Sequence;
        Span<byte> buffer = stackalloc byte[InlineReadBuffer];
        int length = _heap.TryReadVisibleInto(sequence, AmbientVisible, buffer, out _, out _);
        if (length == 0) return false;
        MvccContext.RecordRead(EntityKind.Hyperedge, sequence);
        if (length <= buffer.Length)
            return InlinePropertyCodec.TryScan(buffer[..length], PayloadSize, keyId.Value, out _, out _);
        if (!_heap.TryReadVisible(sequence, AmbientVisible, out var payload, out _, out _)) return false;
        return InlinePropertyCodec.TryScan(payload, PayloadSize, keyId.Value, out _, out _);
    }

    /// <summary>
    /// inline property を set (replace-or-add)。copy-on-write で新 header 版を作る (同一 tx の
    /// 未コミット head は in-place)。inline 不可 (大きすぎ / 予算超過) なら false を返し、
    /// 呼び出し側が overflow チェーンへ回す。
    /// </summary>
    public bool SetInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId, in PropertyValue value)
    {
        if (!InlinePropertyCodec.IsInlineable(value)) return false;
        long sequence = hyperedgeId.Sequence;
        if (!_heap.TryReadVisible(sequence, AmbientVisible, out var current, out _, out _)) return false;
        byte[] next = InlinePropertyCodec.Build(current, PayloadSize, keyId.Value, in value, remove: false);
        if (next.Length > VersionedRecordHeap.MaxPayloadSize) return false; // 予算超過 → overflow
        _heap.AppendOrReplaceHead(sequence, next, MvccContext.CurrentTxId.Value);
        return true;
    }

    public bool RemoveInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId)
    {
        long sequence = hyperedgeId.Sequence;
        if (!_heap.TryReadVisible(sequence, AmbientVisible, out var current, out _, out _)) return false;
        if (!InlinePropertyCodec.TryScan(current, PayloadSize, keyId.Value, out _, out _)) return false;
        byte[] next = InlinePropertyCodec.Build(current, PayloadSize, keyId.Value, default, remove: true);
        _heap.AppendOrReplaceHead(sequence, next, MvccContext.CurrentTxId.Value);
        return true;
    }

    /// <summary>
    /// inline property (可視版) + overflow チェーンを結合して列挙する。inline を先に、
    /// 続いて <paramref name="overflowStore"/> 上の firstProp チェーンを辿る。
    /// </summary>
    public PropertyEnumerator EnumerateProperties(HyperedgeId hyperedgeId, IPropertyStore overflowStore)
    {
        if (!_heap.TryReadVisible(hyperedgeId.Sequence, AmbientVisible, out var payload, out _, out _))
            return new PropertyEnumerator(overflowStore, PropertyId.Invalid);
        MvccContext.RecordRead(EntityKind.Hyperedge, hyperedgeId.Sequence); // property 列挙 = header read
        var firstProp = new PropertyId(RecordHelpers.ReadInt48(payload.AsSpan(OffFirstProperty)));
        return new PropertyEnumerator(payload, overflowStore, firstProp, PayloadSize);
    }

    private static bool AmbientVisible(long xmin, long xmax) => Visibility.IsVisibleAmbient(xmin, xmax);

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

    private long RecomputeInUse()
    {
        long count = 0;
        for (long sequence = 0; sequence < _map.Hwm; sequence++)
        {
            if (_heap.TryReadVisible(
                    sequence, Visibility.IsVisibleAmbient, out _, out _, out _))
                count++;
        }
        return count;
    }

    private static void Validate(
        HyperedgeTypeId type,
        ReadOnlySpan<IncidenceMember> members)
    {
        if (!type.IsValid)
            throw new ArgumentOutOfRangeException(nameof(type));
        if (type.Value > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(type));
        if (members.Length < 2)
            throw new ArgumentException("Hyperedge arity must be at least two.", nameof(members));

        for (int index = 0; index < members.Length; index++)
        {
            IncidenceMember member = members[index];
            if (!member.NodeId.IsValid)
                throw new ArgumentException("A member has an invalid node ID.", nameof(members));
            if (!member.RoleId.IsValid || member.RoleId.Value > short.MaxValue)
                throw new ArgumentException("A member has an invalid role ID.", nameof(members));

            for (int other = 0; other < index; other++)
            {
                if (members[other] == member)
                    throw new ArgumentException(
                        "The same role and node pair cannot occur twice.", nameof(members));
            }
        }
    }

    private static HyperedgeReadHandle NotInUse(HyperedgeId id)
        => new(
            id,
            false,
            HyperedgeTypeId.Invalid,
            IncidenceId.Invalid,
            PropertyId.Invalid,
            0,
            0);
}
