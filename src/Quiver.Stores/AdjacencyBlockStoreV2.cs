using System.Buffers;
using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Stores;

/// <summary>
/// BA-6 / codex_advice_3 §7.2. Read-optimized adjacency view with an inline
/// payload lane (edge weight) so weighted traversal / SSSP / top-k neighbor
/// can avoid the property-chain join for hot-path scalar weights.
///
/// Block page body layout (PageBodySize = 8160 bytes):
///   OutCount(4) | InCount(4) | NextPageId(8) = 16-byte header
///   Followed by OutCount out-entries then InCount in-entries.
///   Entry: TypeId(2) | RelId(6) | NeighborId(6) | Payload(8) = 22 bytes.
///   Max entries per page = (8160 − 16) / 22 = 370.
///
/// Coexists with V1 (AdjacencyBlockStore) — V2 lives in adj_v2.db /
/// adj_v2_idx.dat / adj_v2.meta and is opt-in at bulk-load time via
/// <see cref="BulkLoader.WithPayloadLane"/>. The backend opens whichever
/// pair of files is present, preferring V2 when both exist.
/// </summary>
internal sealed class AdjacencyBlockStoreV2 : IAdjacencyBlockStore, IAdjacencyPayloadView, IDisposable
{
    internal const int BlockHeaderSize = 16;
    internal const int EntrySize = 22; // 2 + 6 + 6 + 8
    internal const int EntriesPerPage = (RecordPageMapping.PageBodySize - BlockHeaderSize) / EntrySize; // 370

    private const uint MetaMagic = 0x32305651; // "QV02" little-endian
    private const ushort MetaVersion = 1;
    internal const int MetaSize = 20; // Magic(4) Version(2) Kind(1) reserved(1) PropKey(4) DefaultRaw(8)

    private readonly IPagedFile _dataFile;
    private readonly FileStream _indexStream;
    private readonly object _idxLock = new();
    private readonly PayloadLaneSpec _spec;

    public PayloadLaneSpec PayloadSpec => _spec;

    internal AdjacencyBlockStoreV2(IPagedFile dataFile, string indexPath, PayloadLaneSpec spec)
    {
        _dataFile = dataFile;
        _indexStream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _spec = spec;
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

    public AdjacencyCursor OpenCursor(NodeId nodeId, Direction direction, RelationshipTypeId? typeFilter)
    {
        long blockPageId = GetBlockPageId(nodeId);
        if (blockPageId < 0) return AdjacencyCursor.Empty;
        return new BlockChainCursorV2(_dataFile, blockPageId, direction, typeFilter);
    }

    /// <summary>
    /// V2-specific read that also copies the payload lane. Returns the count
    /// written. <paramref name="buffer"/> length cap mirrors
    /// <see cref="ReadEdges"/> — callers should drop to <see cref="OpenCursor"/>
    /// when the return value equals <c>buffer.Length</c>.
    /// </summary>
    public int ReadEdgesWithPayload(
        NodeId nodeId, Direction direction, RelationshipTypeId? typeFilter,
        AdjacencyEntryV2[] buffer)
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
                total += CopyEntriesV2(outSpan, typeFilter, buffer, total);
            }
            if (direction is Direction.Incoming or Direction.Both)
            {
                int inOff = BlockHeaderSize + outCount * EntrySize;
                ReadOnlySpan<byte> inSpan = body.Slice(inOff, inCount * EntrySize);
                total += CopyEntriesV2(inSpan, typeFilter, buffer, total);
            }

            blockPageId = nextPageId;
        }
        return total;
    }

    private sealed class BlockChainCursorV2 : AdjacencyCursor
    {
        private byte[] _body;
        private readonly IPagedFile _dataFile;
        private readonly Direction _direction;
        private readonly RelationshipTypeId? _typeFilter;

        private long _nextPageId;
        private int _outCount;
        private int _inCount;
        private int _entryIdx;
        private int _section;
        private bool _pageLoaded;
        private bool _disposed;

        private NodeId _neighbor;
        private RelationshipId _relId;
        private RelationshipTypeId _type;
        private long _weightRaw;

        internal BlockChainCursorV2(IPagedFile dataFile, long firstPageId, Direction direction, RelationshipTypeId? typeFilter)
        {
            _dataFile = dataFile;
            _direction = direction;
            _typeFilter = typeFilter;
            _nextPageId = firstPageId;
            _section = 2;
            _body = ArrayPool<byte>.Shared.Rent(RecordPageMapping.PageBodySize);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_body);
            _body = null!;
        }

        public override NodeId Neighbor => _neighbor;
        public override RelationshipId Relationship => _relId;
        public override RelationshipTypeId Type => _type;
        public override long WeightRaw => _weightRaw;

        public override bool MoveNext()
        {
            while (true)
            {
                if (!_pageLoaded || _section == 2)
                {
                    if (_nextPageId < 0) return false;
                    LoadPage(_nextPageId);
                    _pageLoaded = true;
                    _section = 0;
                    _entryIdx = 0;
                    if (_direction == Direction.Incoming) _section = 1;
                }

                if (_section == 0)
                {
                    while (_entryIdx < _outCount)
                    {
                        int off = BlockHeaderSize + _entryIdx * EntrySize;
                        _entryIdx++;
                        if (TryDecode(off)) return true;
                    }
                    _section = 1;
                    _entryIdx = 0;
                    if (_direction == Direction.Outgoing) _section = 2;
                }

                if (_section == 1)
                {
                    int inBase = BlockHeaderSize + _outCount * EntrySize;
                    while (_entryIdx < _inCount)
                    {
                        int off = inBase + _entryIdx * EntrySize;
                        _entryIdx++;
                        if (TryDecode(off)) return true;
                    }
                    _section = 2;
                }
            }
        }

        private void LoadPage(long pageId)
        {
            using var h = _dataFile.PinForRead(new PageId(pageId));
            ReadOnlySpan<byte> body = h.Data;
            _outCount = BinaryPrimitives.ReadInt32LittleEndian(body);
            _inCount = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
            _nextPageId = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
            int copyLen = BlockHeaderSize + (_outCount + _inCount) * EntrySize;
            body[..copyLen].CopyTo(_body);
        }

        private bool TryDecode(int off)
        {
            Span<byte> e = _body.AsSpan(off);
            var typeId = new RelationshipTypeId(BinaryPrimitives.ReadInt16LittleEndian(e));
            if (_typeFilter.HasValue && typeId != _typeFilter.Value) return false;
            _type = typeId;
            _relId = new RelationshipId(RecordHelpers.ReadInt48(e[2..]));
            _neighbor = new NodeId(RecordHelpers.ReadInt48(e[8..]));
            _weightRaw = BinaryPrimitives.ReadInt64LittleEndian(e[14..]);
            return true;
        }
    }

    // ──────────────────────────── Build ────────────────────────────

    /// <summary>
    /// Build the V2 adjacency index from scratch. <paramref name="weightLookup"/>
    /// maps relationship id to raw 64-bit payload — callers populate this from
    /// pending relationship properties before invoking the build. Edges with no
    /// entry receive <see cref="PayloadLaneSpec.DefaultRaw"/>.
    /// </summary>
    internal static void Build(
        string adjDataPath,
        string adjIndexPath,
        string adjMetaPath,
        IReadOnlyList<(long Id, long Src, long Tgt, int TypeId)> rels,
        IReadOnlyDictionary<long, long> weightLookup,
        long nodeHwm,
        PayloadLaneSpec spec)
    {
        WriteMeta(adjMetaPath, spec);

        using var dataFile = new PagedFile(adjDataPath);
        dataFile.AllocatePage(PageKind.AdjacencyBlock); // page 1 placeholder

        var outEdges = new Dictionary<long, List<(short TypeId, long RelId, long NeighborId, long Payload)>>();
        var inEdges = new Dictionary<long, List<(short TypeId, long RelId, long NeighborId, long Payload)>>();

        foreach (var (id, src, tgt, typeId) in rels)
        {
            long payload = weightLookup.TryGetValue(id, out var w) ? w : spec.DefaultRaw;
            GetOrAdd(outEdges, src).Add(((short)typeId, id, tgt, payload));
            if (src != tgt)
                GetOrAdd(inEdges, tgt).Add(((short)typeId, id, src, payload));
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

    internal static PayloadLaneSpec ReadMeta(string metaPath)
    {
        Span<byte> buf = stackalloc byte[MetaSize];
        using var fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.ReadExactly(buf);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        if (magic != MetaMagic)
            throw new InvalidDataException($"adj_v2.meta: bad magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(buf[4..]);
        if (version != MetaVersion)
            throw new InvalidDataException($"adj_v2.meta: unsupported version {version}");
        var kind = (PayloadKind)buf[6];
        int propKey = BinaryPrimitives.ReadInt32LittleEndian(buf[8..]);
        long defaultRaw = BinaryPrimitives.ReadInt64LittleEndian(buf[12..]);
        return new PayloadLaneSpec(kind, propKey, defaultRaw);
    }

    private static void WriteMeta(string metaPath, PayloadLaneSpec spec)
    {
        Span<byte> buf = stackalloc byte[MetaSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, MetaMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[4..], MetaVersion);
        buf[6] = (byte)spec.Kind;
        buf[7] = 0; // reserved
        BinaryPrimitives.WriteInt32LittleEndian(buf[8..], spec.PropertyKeyId);
        BinaryPrimitives.WriteInt64LittleEndian(buf[12..], spec.DefaultRaw);
        using var fs = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(buf);
        fs.Flush();
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

    private static int CopyEntriesV2(
        ReadOnlySpan<byte> span, RelationshipTypeId? typeFilter,
        AdjacencyEntryV2[] buffer, int offset)
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
            long payload = BinaryPrimitives.ReadInt64LittleEndian(e[14..]);
            buffer[offset + written++] = new AdjacencyEntryV2(typeId, relId, neighborId, payload);
        }
        return written;
    }

    private static long WriteBlocks(
        IPagedFile dataFile,
        List<(short TypeId, long RelId, long NeighborId, long Payload)> outs,
        List<(short TypeId, long RelId, long NeighborId, long Payload)> ins)
    {
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

        var pageIds = new long[pages.Count];
        for (int p = 0; p < pages.Count; p++)
            pageIds[p] = dataFile.AllocatePage(PageKind.AdjacencyBlock).Value;

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
                BinaryPrimitives.WriteInt64LittleEndian(body[(off + 14)..], e.Payload);
            }
            for (int i = 0; i < inCount; i++, inIdx++, off += EntrySize)
            {
                var e = ins[inIdx];
                BinaryPrimitives.WriteInt16LittleEndian(body[off..], e.TypeId);
                RecordHelpers.WriteInt48(body[(off + 2)..], e.RelId);
                RecordHelpers.WriteInt48(body[(off + 8)..], e.NeighborId);
                BinaryPrimitives.WriteInt64LittleEndian(body[(off + 14)..], e.Payload);
            }
            ph.Dispose();
        }

        return pageIds[0];
    }

    private static List<(short TypeId, long RelId, long NeighborId, long Payload)> GetOrAdd(
        Dictionary<long, List<(short TypeId, long RelId, long NeighborId, long Payload)>> dict, long key)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = [];
        return list;
    }

    public void Dispose() => _indexStream.Dispose();
}
