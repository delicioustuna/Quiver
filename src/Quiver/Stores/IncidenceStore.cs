using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// 33 バイト固定 payload の incidence store。incidence 自身は MVCC entity ではなく、
/// 可視性は参照先 hyperedge header に従う。
///
/// <para>incidence payload (33B, version ヘッダ 24B の後ろ):</para>
/// <code>
///   [0]  flags           : u8    (FlagInUse)
///   [1]  hyperedge       : Int48 (Sequence)
///   [7]  node            : Int48 (Sequence)
///   [13] role            : i16
///   [15] prevInNode      : Int48 (Sequence)
///   [21] nextInNode      : Int48 (Sequence)
///   [27] nextInHyperedge : Int48 (Sequence)
/// </code>
/// </summary>
internal sealed class IncidenceStore : IIncidenceStore
{
    // prevInNode は node chain からの unlink を chain 再走査なしで行うために持つ。
    // メンバー集合は不変で hyperedge chain は header ごと消えるため prevInHyperedge は無い。
    private const int PayloadSize = 33;
    private const int OffFlags = 0;
    private const int OffHyperedge = 1;
    private const int OffNode = 7;
    private const int OffRole = 13;
    private const int OffPreviousInNode = 15;
    private const int OffNextInNode = 21;
    private const int OffNextInHyperedge = 27;
    private const byte FlagInUse = 0x01;

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private readonly VersionedRecordHeap _heap;
    private long _inUseCount;

    public IncidenceStore(IPagedFile heapFile, ItemPointerMap map)
    {
        _file = heapFile;
        _map = map;
        _heap = new VersionedRecordHeap(heapFile, map);
        _inUseCount = RecomputeInUse();
    }

    public long InUseCount => _inUseCount;

    public IncidenceId Allocate(
        HyperedgeId hyperedgeId,
        NodeId nodeId,
        RoleId roleId,
        IncidenceId previousInNode,
        IncidenceId nextInNode,
        IncidenceId nextInHyperedge)
    {
        long sequence = _map.PopFreeSeq();
        if (sequence < 0)
            sequence = _map.Hwm;

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload.Clear();
        payload[OffFlags] = FlagInUse;
        RecordHelpers.WriteInt48(payload[OffHyperedge..], hyperedgeId.Sequence);
        RecordHelpers.WriteInt48(payload[OffNode..], nodeId.Sequence);
        BinaryPrimitives.WriteInt16LittleEndian(payload[OffRole..], checked((short)roleId.Value));
        RecordHelpers.WriteInt48(payload[OffPreviousInNode..], previousInNode.Sequence);
        RecordHelpers.WriteInt48(payload[OffNextInNode..], nextInNode.Sequence);
        RecordHelpers.WriteInt48(payload[OffNextInHyperedge..], nextInHyperedge.Sequence);

        _heap.Insert(sequence, payload, MvccContext.CurrentTxId.Value);
        _inUseCount++;
        return new IncidenceId(sequence);
    }

    public IncidenceReadHandle Read(IncidenceId incidenceId)
    {
        long sequence = incidenceId.Sequence;
        if (sequence < 0 || sequence >= _map.Hwm)
            return NotInUse(incidenceId);

        Span<byte> payload = stackalloc byte[PayloadSize];
        int length = _heap.TryReadHeadInto(sequence, payload, out _, out _, out _);
        if (length < PayloadSize)
            return NotInUse(incidenceId);

        return new IncidenceReadHandle(
            incidenceId,
            (payload[OffFlags] & FlagInUse) != 0,
            new HyperedgeId(RecordHelpers.ReadInt48(payload[OffHyperedge..])),
            new NodeId(RecordHelpers.ReadInt48(payload[OffNode..])),
            new RoleId(BinaryPrimitives.ReadInt16LittleEndian(payload[OffRole..])),
            new IncidenceId(RecordHelpers.ReadInt48(payload[OffPreviousInNode..])),
            new IncidenceId(RecordHelpers.ReadInt48(payload[OffNextInNode..])),
            new IncidenceId(RecordHelpers.ReadInt48(payload[OffNextInHyperedge..])));
    }

    public IncidenceWriteHandle Write(IncidenceId incidenceId)
    {
        ItemPointer pointer = _heap.GetHead(incidenceId.Sequence);
        if (pointer.IsNull)
            throw new CorruptionException($"Write on missing incidence seq={incidenceId.Sequence}.");

        var pageId = new PageId(pointer.PageId);
        var page = _file.PinForWrite(pageId);
        var slottedPage = new SlottedPage(page.Data);
        if (!slottedPage.TryGetMutable(pointer.Slot, out Span<byte> record))
        {
            _file.Unpin(pageId);
            throw new CorruptionException(
                $"Missing version slot for incidence seq={incidenceId.Sequence}.");
        }

        return new IncidenceWriteHandle(
            _file,
            pageId,
            record.Slice(VersionedRecordHeap.VersionHeaderSize, PayloadSize));
    }

    public NodeIncidenceEnumerator EnumerateByNode(
        NodeId nodeId,
        INodeIncidenceHeadStore nodeHeads,
        IHyperedgeStore hyperedges)
        => new(this, hyperedges, nodeHeads.Get(nodeId));

    public HyperedgeIncidenceEnumerator EnumerateByHyperedge(
        HyperedgeId hyperedgeId,
        IHyperedgeStore hyperedges)
    {
        using var header = hyperedges.Read(hyperedgeId);
        return new HyperedgeIncidenceEnumerator(
            this, hyperedges, hyperedgeId, header.FirstIncidenceId);
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
            using var record = Read(new IncidenceId(sequence));
            if (record.InUse)
                count++;
        }

        return count;
    }

    private static IncidenceReadHandle NotInUse(IncidenceId id)
        => new(
            id,
            false,
            HyperedgeId.Invalid,
            NodeId.Invalid,
            RoleId.Invalid,
            IncidenceId.Invalid,
            IncidenceId.Invalid,
            IncidenceId.Invalid);
}
