using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Stores;

/// <summary>
/// Contiguous adjacency block store: for each node stores out-edges and in-edges
/// sorted by TypeId in consecutive pages of adj.db. A parallel adj_idx.dat array maps
/// NodeId → first block PageId (int64, −1 if not indexed).
///
/// Block page body layout (PageBodySize = 8160 bytes):
///   OutCount(4) | InCount(4) | NextPageId(8) = 16-byte header
///   Followed by OutCount out-entries then InCount in-entries.
///   Entry: TypeId(2) | RelId(6) | NeighborId(6) = 14 bytes.
///   Max entries per page = (8160 − 16) / 14 = 581.
/// </summary>
internal sealed class AdjacencyBlockStore : IAdjacencyBlockStore, IDisposable
{
    internal const int BlockHeaderSize = 16;
    internal const int EntrySize = 14;
    internal const int EntriesPerPage = (RecordPageMapping.PageBodySize - BlockHeaderSize) / EntrySize; // 581

    private static readonly PageId StoreHeaderPageId = new(1);

    private readonly IPagedFile _dataFile;
    private readonly FileStream _indexStream;
    private readonly object _idxLock = new();

    internal AdjacencyBlockStore(IPagedFile dataFile, string indexPath)
    {
        _dataFile = dataFile;
        _indexStream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    // ──────────────────────────── IAdjacencyBlockStore ────────────────────────────

    public bool HasBlock(NodeId nodeId) => GetBlockPageId(nodeId) >= 0;

    public int ReadEdges(NodeId nodeId, Direction direction, RelationshipTypeId? typeFilter, AdjacencyEntry[] buffer)
    {
        long blockPageId = GetBlockPageId(nodeId);
        if (blockPageId < 0) return 0;

        int total = 0;
        while (blockPageId >= 0 && total < buffer.Length)
        {
            using var h = _dataFile.PinForRead(new PageId(blockPageId));
            ReadOnlySpan<byte> body = h.Data;

            int outCount = BinaryPrimitives.ReadInt32LittleEndian(body);
            int inCount = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
            long nextPageId = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);

            if (direction is Direction.Outgoing or Direction.Both)
            {
                ReadOnlySpan<byte> outSpan = body.Slice(BlockHeaderSize, outCount * EntrySize);
                total += CopyEntries(outSpan, typeFilter, buffer, total);
            }
            if (direction is Direction.Incoming or Direction.Both)
            {
                int inOff = BlockHeaderSize + outCount * EntrySize;
                ReadOnlySpan<byte> inSpan = body.Slice(inOff, inCount * EntrySize);
                total += CopyEntries(inSpan, typeFilter, buffer, total);
            }

            blockPageId = nextPageId;
        }
        return total;
    }

    // ──────────────────────────── Build ────────────────────────────

    /// <summary>
    /// Build the adjacency index from scratch. Called by BulkLoader.Commit when
    /// adjacency index building is requested.
    /// </summary>
    internal static void Build(
        string adjDataPath,
        string adjIndexPath,
        IReadOnlyList<(long Id, long Src, long Tgt, int TypeId)> rels,
        long nodeHwm)
    {
        using var dataFile = new PagedFile(adjDataPath);
        dataFile.AllocatePage(PageKind.AdjacencyBlock); // page 1 = store header placeholder

        var outEdges = new Dictionary<long, List<(short TypeId, long RelId, long NeighborId)>>();
        var inEdges = new Dictionary<long, List<(short TypeId, long RelId, long NeighborId)>>();

        foreach (var (id, src, tgt, typeId) in rels)
        {
            GetOrAdd(outEdges, src).Add(((short)typeId, id, tgt));
            if (src != tgt)
                GetOrAdd(inEdges, tgt).Add(((short)typeId, id, src));
        }

        foreach (var list in outEdges.Values) list.Sort((a, b) => a.TypeId.CompareTo(b.TypeId));
        foreach (var list in inEdges.Values) list.Sort((a, b) => a.TypeId.CompareTo(b.TypeId));

        using var idxStream = new FileStream(adjIndexPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> idxEntry = stackalloc byte[8];

        for (long nodeId = 0; nodeId < nodeHwm; nodeId++)
        {
            outEdges.TryGetValue(nodeId, out var outs);
            inEdges.TryGetValue(nodeId, out var ins);

            long firstPageId = -1;
            if ((outs?.Count ?? 0) > 0 || (ins?.Count ?? 0) > 0)
                firstPageId = WriteBlocks(dataFile, outs ?? [], ins ?? []);

            BinaryPrimitives.WriteInt64LittleEndian(idxEntry, firstPageId);
            idxStream.Write(idxEntry);
        }

        idxStream.Flush();
    }

    // ──────────────────────────── private ────────────────────────────

    private long GetBlockPageId(NodeId nodeId)
    {
        long offset = nodeId.Value * 8;
        lock (_idxLock)
        {
            if (_indexStream.Length < offset + 8) return -1;
            _indexStream.Seek(offset, SeekOrigin.Begin);
            Span<byte> buf = stackalloc byte[8];
            _indexStream.ReadExactly(buf);
            return BinaryPrimitives.ReadInt64LittleEndian(buf);
        }
    }

    private static int CopyEntries(
        ReadOnlySpan<byte> span, RelationshipTypeId? typeFilter,
        AdjacencyEntry[] buffer, int offset)
    {
        int count = span.Length / EntrySize;
        int written = 0;
        for (int i = 0; i < count && offset + written < buffer.Length; i++)
        {
            ReadOnlySpan<byte> e = span[(i * EntrySize)..];
            var typeId = new RelationshipTypeId(BinaryPrimitives.ReadInt16LittleEndian(e));
            if (typeFilter.HasValue && typeId != typeFilter.Value) continue;
            var relId = new RelationshipId(RecordHelpers.ReadInt48(e[2..]));
            var neighborId = new NodeId(RecordHelpers.ReadInt48(e[8..]));
            buffer[offset + written++] = new AdjacencyEntry(typeId, relId, neighborId);
        }
        return written;
    }

    private static long WriteBlocks(
        IPagedFile dataFile,
        List<(short TypeId, long RelId, long NeighborId)> outs,
        List<(short TypeId, long RelId, long NeighborId)> ins)
    {
        // Pre-plan pages so we know NextPageId before writing each page.
        var pages = new List<(int OutCount, int InCount)>();
        int outIdx = 0, inIdx = 0;
        while (outIdx < outs.Count || inIdx < ins.Count)
        {
            int outThis = Math.Min(outs.Count - outIdx, EntriesPerPage);
            int inThis = Math.Min(ins.Count - inIdx, EntriesPerPage - outThis);
            pages.Add((outThis, inThis));
            outIdx += outThis;
            inIdx += inThis;
        }

        // Allocate all pages upfront.
        var pageIds = new long[pages.Count];
        for (int p = 0; p < pages.Count; p++)
            pageIds[p] = dataFile.AllocatePage(PageKind.AdjacencyBlock).Value;

        // Write content.
        outIdx = 0; inIdx = 0;
        for (int p = 0; p < pages.Count; p++)
        {
            var (outCount, inCount) = pages[p];
            long nextPageId = p + 1 < pages.Count ? pageIds[p + 1] : -1L;
            var pageId = new PageId(pageIds[p]);

            var ph = dataFile.PinForWrite(pageId);
            Span<byte> body = ph.Data;
            body.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(body, outCount);
            BinaryPrimitives.WriteInt32LittleEndian(body[4..], inCount);
            BinaryPrimitives.WriteInt64LittleEndian(body[8..], nextPageId);

            int off = BlockHeaderSize;
            for (int i = 0; i < outCount; i++, outIdx++, off += EntrySize)
            {
                var e = outs[outIdx];
                BinaryPrimitives.WriteInt16LittleEndian(body[off..], e.TypeId);
                RecordHelpers.WriteInt48(body[(off + 2)..], e.RelId);
                RecordHelpers.WriteInt48(body[(off + 8)..], e.NeighborId);
            }
            for (int i = 0; i < inCount; i++, inIdx++, off += EntrySize)
            {
                var e = ins[inIdx];
                BinaryPrimitives.WriteInt16LittleEndian(body[off..], e.TypeId);
                RecordHelpers.WriteInt48(body[(off + 2)..], e.RelId);
                RecordHelpers.WriteInt48(body[(off + 8)..], e.NeighborId);
            }
            ph.Dispose();
        }

        return pageIds[0];
    }

    private static List<(short TypeId, long RelId, long NeighborId)> GetOrAdd(
        Dictionary<long, List<(short TypeId, long RelId, long NeighborId)>> dict, long key)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = [];
        return list;
    }

    public void Dispose() => _indexStream.Dispose();
}
