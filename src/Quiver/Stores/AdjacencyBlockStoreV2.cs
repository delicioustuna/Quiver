using System.Buffers;
using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// インライン payload lane (エッジ重み) を持つ
/// 読み取り最適化済みの隣接ビュー。重み付きトラバーサル / SSSP / top-k 近傍などの
/// hot path スカラ重みでプロパティチェーンへのジョインを避けられる。
///
/// ブロックページ本体レイアウト (PageBodySize = 8160 バイト):
///   OutCount(4) | InCount(4) | NextPageId(8) = 16 バイトのヘッダ
///   続いて OutCount 個の out エントリ、その後 InCount 個の in エントリ。
///   エントリ: TypeId(2) | EdgeId(6) | NeighborId(6) | Payload(8) = 22 バイト。
///   1 ページあたり最大エントリ数 = (8160 − 16) / 22 = 370。
///
/// V1 (AdjacencyBlockStore) と排他 — バルクロード時の <see cref="BulkLoader.WithPayloadLane"/> で
/// V2 をオプトインする。V1/V2 とも graph.quiver 内の同一テナント
/// (<see cref="AdjacencyContainer.DataTenant"/>) に格納され、種別は DataTenant の記述子で判別する。
/// </summary>
internal sealed class AdjacencyBlockStoreV2 : IAdjacencyBlockStore, IAdjacencyPayloadView, IDisposable
{
    internal const int BlockHeaderSize = 16;
    internal const int EntrySize = 22; // 2 + 6 + 6 + 8
    internal const int EntriesPerPage = (RecordPageMapping.PageBodySize - BlockHeaderSize) / EntrySize; // 370

    private readonly IPagedFile _dataFile;
    private readonly IPagedFile _indexFile;
    private readonly long _idxEntryCount;
    private readonly PayloadLaneSpec _spec;
    private readonly AdjacencyEpoch? _epoch;

    public PayloadLaneSpec PayloadSpec => _spec;

    internal AdjacencyBlockStoreV2(IPagedFile dataFile, IPagedFile indexFile, PayloadLaneSpec spec,
        AdjacencyEpoch? epoch = null)
    {
        _dataFile = dataFile;
        _indexFile = indexFile;
        _idxEntryCount = AdjacencyContainer.ReadIndexEntryCount(indexFile);
        _spec = spec;
        _epoch = epoch;
    }

    public long Epoch => _epoch?.Epoch ?? 0;
    public long BaseEdgeHwm => _epoch?.BaseEdgeHwm ?? 0;
    // tombstone epoch のキーは Sequence (packed Value ではない)。
    public bool IsTombstoned(EdgeId edgeId) => _epoch?.IsTombstoned(edgeId.Sequence) ?? false;
    public void Tombstone(EdgeId edgeId) => _epoch?.Tombstone(edgeId.Sequence);

    // ──────────────────────────── IAdjacencyBlockStore ────────────────────────────

    public bool HasBlock(VertexId vertexId) => GetBlockPageId(vertexId) >= 0;

    public int ReadEdges(VertexId vertexId, Direction direction, EdgeTypeId? typeFilter, AdjacencyEntry[] buffer)
    {
        long blockPageId = GetBlockPageId(vertexId);
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

    public AdjacencyCursor OpenCursor(VertexId vertexId, Direction direction, EdgeTypeId? typeFilter)
    {
        long blockPageId = GetBlockPageId(vertexId);
        if (blockPageId < 0) return AdjacencyCursor.Empty;
        return new BlockChainCursorV2(_dataFile, blockPageId, direction, typeFilter);
    }

    /// <summary>
    /// payload lane もコピーする V2 固有の読み取り。書き込み件数を返す。
    /// <paramref name="buffer"/> 長の上限は <see cref="ReadEdges"/> と同じ —
    /// 戻り値が <c>buffer.Length</c> と等しい場合は <see cref="OpenCursor"/> へ降格すべき。
    /// </summary>
    public int ReadEdgesWithPayload(
        VertexId vertexId, Direction direction, EdgeTypeId? typeFilter,
        AdjacencyEntryV2[] buffer)
    {
        long blockPageId = GetBlockPageId(vertexId);
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
        private readonly EdgeTypeId? _typeFilter;

        private long _nextPageId;
        private int _outCount;
        private int _inCount;
        private int _entryIdx;
        private int _section;
        private bool _pageLoaded;
        private bool _disposed;

        private VertexId _neighbor;
        private EdgeId _edgeId;
        private EdgeTypeId _type;
        private long _weightRaw;

        internal BlockChainCursorV2(IPagedFile dataFile, long firstPageId, Direction direction, EdgeTypeId? typeFilter)
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

        public override VertexId Neighbor => _neighbor;
        public override EdgeId Edge => _edgeId;
        public override EdgeTypeId Type => _type;
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
            var typeId = new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(e));
            if (_typeFilter.HasValue && typeId != _typeFilter.Value) return false;
            _type = typeId;
            _edgeId = new EdgeId(RecordHelpers.ReadInt48(e[2..]));
            _neighbor = new VertexId(RecordHelpers.ReadInt48(e[8..]));
            _weightRaw = BinaryPrimitives.ReadInt64LittleEndian(e[14..]);
            return true;
        }
    }

    // ──────────────────────────── Build ────────────────────────────

    /// <summary>
    /// V2 隣接インデックスをゼロから構築する。<paramref name="weightLookup"/> は
    /// Edge ID → 64 ビット生 payload のマップで、呼び出し側はビルド呼び出し前に
    /// 保留中のEdge・プロパティから埋めておく。エントリの無いエッジには
    /// <see cref="PayloadLaneSpec.DefaultRaw"/> が割り当てられる。
    /// </summary>
    internal static void Build(
        IPagedFile dataFile,
        IPagedFile indexFile,
        IReadOnlyList<(long Id, long Src, long Tgt, int TypeId)> edges,
        IReadOnlyDictionary<long, long> weightLookup,
        long vertexHwm,
        PayloadLaneSpec spec,
        bool writeDescriptor = true)
    {
        dataFile.Truncate(1);
        dataFile.AllocatePage(PageKind.AdjacencyBlock); // logical page 1 = 記述子 placeholder

        var outEdges = new Dictionary<long, List<(short TypeId, long EdgeId, long NeighborId, long Payload)>>();
        var inEdges = new Dictionary<long, List<(short TypeId, long EdgeId, long NeighborId, long Payload)>>();

        foreach (var (id, src, tgt, typeId) in edges)
        {
            long payload = weightLookup.TryGetValue(id, out var w) ? w : spec.DefaultRaw;
            GetOrAdd(outEdges, src).Add(((short)typeId, id, tgt, payload));
            if (src != tgt)
                GetOrAdd(inEdges, tgt).Add(((short)typeId, id, src, payload));
        }

        foreach (var list in outEdges.Values) list.Sort((a, b) => a.TypeId.CompareTo(b.TypeId));
        foreach (var list in inEdges.Values) list.Sort((a, b) => a.TypeId.CompareTo(b.TypeId));

        var firstPageIds = new long[vertexHwm];
        for (long vertexId = 0; vertexId < vertexHwm; vertexId++)
        {
            outEdges.TryGetValue(vertexId, out var outs);
            inEdges.TryGetValue(vertexId, out var ins);

            long firstPageId = -1;
            if ((outs?.Count ?? 0) > 0 || (ins?.Count ?? 0) > 0)
                firstPageId = WriteBlocks(dataFile, outs ?? [], ins ?? []);
            firstPageIds[vertexId] = firstPageId;
        }

        if (writeDescriptor)
            AdjacencyContainer.WriteDescriptor(dataFile, AdjacencyContainer.KindV2, spec);
        AdjacencyContainer.WriteIndex(indexFile, firstPageIds);
    }

    // ──────────────────────────── private ────────────────────────────

    private long GetBlockPageId(VertexId vertexId)
        => AdjacencyContainer.ReadIndexEntry(_indexFile, _idxEntryCount, vertexId.Sequence); // index キーは Sequence

    private static int CopyEntries(
        ReadOnlySpan<byte> span, EdgeTypeId? typeFilter,
        AdjacencyEntry[] buffer, int offset)
    {
        int count = span.Length / EntrySize;
        int written = 0;
        for (int i = 0; i < count && offset + written < buffer.Length; i++)
        {
            ReadOnlySpan<byte> e = span[(i * EntrySize)..];
            var typeId = new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(e));
            if (typeFilter.HasValue && typeId != typeFilter.Value) continue;
            var edgeId = new EdgeId(RecordHelpers.ReadInt48(e[2..]));
            var neighborId = new VertexId(RecordHelpers.ReadInt48(e[8..]));
            buffer[offset + written++] = new AdjacencyEntry(typeId, edgeId, neighborId);
        }
        return written;
    }

    private static int CopyEntriesV2(
        ReadOnlySpan<byte> span, EdgeTypeId? typeFilter,
        AdjacencyEntryV2[] buffer, int offset)
    {
        int count = span.Length / EntrySize;
        int written = 0;
        for (int i = 0; i < count && offset + written < buffer.Length; i++)
        {
            ReadOnlySpan<byte> e = span[(i * EntrySize)..];
            var typeId = new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(e));
            if (typeFilter.HasValue && typeId != typeFilter.Value) continue;
            var edgeId = new EdgeId(RecordHelpers.ReadInt48(e[2..]));
            var neighborId = new VertexId(RecordHelpers.ReadInt48(e[8..]));
            long payload = BinaryPrimitives.ReadInt64LittleEndian(e[14..]);
            buffer[offset + written++] = new AdjacencyEntryV2(typeId, edgeId, neighborId, payload);
        }
        return written;
    }

    private static long WriteBlocks(
        IPagedFile dataFile,
        List<(short TypeId, long EdgeId, long NeighborId, long Payload)> outs,
        List<(short TypeId, long EdgeId, long NeighborId, long Payload)> ins)
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
                RecordHelpers.WriteInt48(body[(off + 2)..], e.EdgeId);
                RecordHelpers.WriteInt48(body[(off + 8)..], e.NeighborId);
                BinaryPrimitives.WriteInt64LittleEndian(body[(off + 14)..], e.Payload);
            }
            for (int i = 0; i < inCount; i++, inIdx++, off += EntrySize)
            {
                var e = ins[inIdx];
                BinaryPrimitives.WriteInt16LittleEndian(body[off..], e.TypeId);
                RecordHelpers.WriteInt48(body[(off + 2)..], e.EdgeId);
                RecordHelpers.WriteInt48(body[(off + 8)..], e.NeighborId);
                BinaryPrimitives.WriteInt64LittleEndian(body[(off + 14)..], e.Payload);
            }
            ph.Dispose();
        }

        return pageIds[0];
    }

    private static List<(short TypeId, long EdgeId, long NeighborId, long Payload)> GetOrAdd(
        Dictionary<long, List<(short TypeId, long EdgeId, long NeighborId, long Payload)>> dict, long key)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = [];
        return list;
    }

    // テナントは container が所有する。dispose は no-op。
    public void Dispose() { }
}
