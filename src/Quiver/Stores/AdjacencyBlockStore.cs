using System.Buffers;
using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// 連続配置の隣接ブロックストア: 各ノードについて TypeId 順にソートされた out-edge と
/// in-edge を <see cref="AdjacencyContainer.DataTenant"/> の連続ページに保持する。並列の
/// <see cref="AdjacencyContainer.IndexTenant"/> が NodeId → 先頭ブロック論理 PageId
/// (int64、未索引なら −1) を保持する。
///
/// ブロックページ本体レイアウト (PageBodySize = 8160 バイト):
///   OutCount(4) | InCount(4) | NextPageId(8) = 16 バイトのヘッダ
///   続いて OutCount 個の out エントリ、その後 InCount 個の in エントリ。
///   エントリ: TypeId(2) | RelId(6) | NeighborId(6) = 14 バイト。
///   1 ページあたり最大エントリ数 = (8160 − 16) / 14 = 581。
///
/// ARCH-4 増分6: 旧来の adj.db / adj_idx.dat サイドカーは廃止され、データ・索引とも
/// graph.quiver 内のテナント (IPagedFile) に同居する。
/// </summary>
internal sealed class AdjacencyBlockStore : IAdjacencyBlockStore, IDisposable
{
    internal const int BlockHeaderSize = 16;
    internal const int EntrySize = 14;
    internal const int EntriesPerPage = (RecordPageMapping.PageBodySize - BlockHeaderSize) / EntrySize; // 581

    private readonly IPagedFile _dataFile;
    private readonly IPagedFile _indexFile;
    private readonly long _idxEntryCount;
    private readonly AdjacencyEpoch? _epoch;

    internal AdjacencyBlockStore(IPagedFile dataFile, IPagedFile indexFile, AdjacencyEpoch? epoch = null)
    {
        _dataFile = dataFile;
        _indexFile = indexFile;
        _idxEntryCount = AdjacencyContainer.ReadIndexEntryCount(indexFile);
        _epoch = epoch;
    }

    public long Epoch => _epoch?.Epoch ?? 0;
    public long BaseRelHwm => _epoch?.BaseRelHwm ?? 0;
    // ARCH-5b: tombstone epoch のキーは Sequence (packed Value ではない)。
    public bool IsTombstoned(RelationshipId relId) => _epoch?.IsTombstoned(relId.Sequence) ?? false;
    public void Tombstone(RelationshipId relId) => _epoch?.Tombstone(relId.Sequence);

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
        return new BlockChainCursor(_dataFile, blockPageId, direction, typeFilter);
    }

    private sealed class BlockChainCursor : AdjacencyCursor
    {
        // ページ本体は 8160 バイトで、PageReadHandle は ref struct のため、MoveNext 間で状態を
        // 保持するには訪問したページをヒープバッファにコピーする必要がある。バッファは共有 ArrayPool から
        // レンタルし、カーソルごとの ~8KB アロケーションを回避する — Dispose で返却される。
        private byte[] _body;
        private readonly IPagedFile _dataFile;
        private readonly Direction _direction;
        private readonly RelationshipTypeId? _typeFilter;

        private long _nextPageId;     // 現在のページを使い切ったときに読み込む次ページ (−1 = なし)
        private int _outCount;
        private int _inCount;
        private int _entryIdx;        // 現在セクション内のインデックス
        private int _section;         // 0 = out, 1 = in, 2 = ページ消化済み
        private bool _pageLoaded;
        private bool _disposed;

        private NodeId _neighbor;
        private RelationshipId _relId;
        private RelationshipTypeId _type;

        internal BlockChainCursor(IPagedFile dataFile, long firstPageId, Direction direction, RelationshipTypeId? typeFilter)
        {
            _dataFile = dataFile;
            _direction = direction;
            _typeFilter = typeFilter;
            _nextPageId = firstPageId;
            _section = 2; // 最初の MoveNext でページロードを強制する
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
                    // incoming のみが要求された場合は out セクションを完全にスキップする。
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
                    // outgoing のみが要求された場合は in セクションをスキップする。
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
            return true;
        }
    }

    // ──────────────────────────── Build ────────────────────────────

    /// <summary>
    /// 隣接インデックスをゼロから構築する。BulkLoader.Commit / CompactAdjacency が要求したときに呼ばれる。
    /// ARCH-4 増分6: 既存の <paramref name="dataFile"/> / <paramref name="indexFile"/> テナントを
    /// 一旦 truncate して作り直す (graph.quiver に同居)。
    /// </summary>
    internal static void Build(
        IPagedFile dataFile,
        IPagedFile indexFile,
        IReadOnlyList<(long Id, long Src, long Tgt, int TypeId)> rels,
        long nodeHwm)
    {
        dataFile.Truncate(1);
        dataFile.AllocatePage(PageKind.AdjacencyBlock); // logical page 1 = 記述子 placeholder

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

        var firstPageIds = new long[nodeHwm];
        for (long nodeId = 0; nodeId < nodeHwm; nodeId++)
        {
            outEdges.TryGetValue(nodeId, out var outs);
            inEdges.TryGetValue(nodeId, out var ins);

            long firstPageId = -1;
            if ((outs?.Count ?? 0) > 0 || (ins?.Count ?? 0) > 0)
                firstPageId = WriteBlocks(dataFile, outs ?? [], ins ?? []);
            firstPageIds[nodeId] = firstPageId;
        }

        AdjacencyContainer.WriteDescriptor(dataFile, AdjacencyContainer.KindV1, null);
        AdjacencyContainer.WriteIndex(indexFile, firstPageIds);
    }

    // ──────────────────────────── private ────────────────────────────

    private long GetBlockPageId(NodeId nodeId)
        => AdjacencyContainer.ReadIndexEntry(_indexFile, _idxEntryCount, nodeId.Sequence); // ARCH-5b: index キーは Sequence

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

    // テナントは container が所有する。dispose は no-op。
    public void Dispose() { }
}
