using System.Buffers.Binary;
using Yatagarasu.Core;
using Yatagarasu.Storage;

namespace Yatagarasu.Storage.Records;

/// <summary>
/// adjacency segment、その vertex→firstPageId 索引、epoch メタを
/// 単一 <c>graph.yata</c> コンテナ内のテナントへ配置する共有レイアウトヘルパ。
///
/// テナント割当 (factory コア 1..10 / IndexManager 0x3F + 0x40.. と衝突しない予約):
/// <list type="bullet">
///   <item><see cref="DataTenant"/> = segment page。論理 page 1 に記述子と payload spec、
///     論理 page 2+ に隣接ブロック。</item>
///   <item><see cref="IndexTenant"/> = VertexId → 先頭ブロック論理 PageId の int64 配列。
///     論理 page 1 に entryCount、論理 page 2+ に int64 エントリ (1 ページ 1020 件)。</item>
///   <item><see cref="EpochTenant"/> = <see cref="AdjacencyEpoch"/> (epoch / baseEdgeHwm / tombstones)。</item>
/// </list>
/// </summary>
internal static class AdjacencyContainer
{
    public const byte DataTenant = 11;
    public const byte IndexTenant = 12;
    public const byte EpochTenant = 13;

    private static readonly Dictionary<long, long> EmptyWeights = new();

    /// <summary>
    /// bulk load 後に隣接ビューを container テナントへ構築し epoch を初期化する。
    /// payload lane 未指定時も <see cref="PayloadKind.None"/> の同一 segment format を使う。
    /// bulk load は WAL を介さないため、構築したページを durable にするよう最後に container を flush する。
    /// </summary>
    public static void Build(
        SingleFileContainer container,
        IReadOnlyList<(long Id, long Src, long Tgt, int TypeId)> relData,
        long vertexHwm,
        long relHwm,
        PayloadLaneSpec? spec,
        IReadOnlyDictionary<long, long>? weights)
    {
        var data = container.OpenTenant(DataTenant, PageKind.AdjacencyBlock);
        var idx = container.OpenTenant(IndexTenant, PageKind.Header);
        var epochTenant = container.OpenTenant(EpochTenant, PageKind.Header);

        PayloadLaneSpec effectiveSpec = spec ?? new PayloadLaneSpec(PayloadKind.None, -1, 0);
        AdjacencySegmentStore.Build(
            data, idx, relData, weights ?? EmptyWeights, vertexHwm, effectiveSpec);

        AdjacencyEpoch.CreateNew(epochTenant, relHwm);
        container.Flush();
    }

    // ── DataTenant 記述子 (論理 page 1 body) ──
    private const uint DescMagic = 0x4A444151;   // "QADJ"
    private const ushort DescVersion = 2;
    public const byte KindNone = 0;
    public const byte KindSegment = 1;

    // ── IndexTenant レイアウト ──
    public const int IndexEntriesPerPage = RecordPageMapping.PageBodySize / 8; // 1020
    private static readonly PageId IndexHeaderPage = new(1);

    // ──────────────────────────────────────────────────────────────────
    // DataTenant 記述子
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// DataTenant の論理 page 1 へ記述子を書く。Build が page 1 を placeholder として確保した後に呼ぶ。
    /// </summary>
    public static void WriteDescriptor(IPagedFile data, byte kind, PayloadLaneSpec? spec)
    {
        while (data.PageCount <= 1)
            data.AllocatePage(PageKind.AdjacencyBlock);
        var wh = data.PinForWrite(new PageId(1));
        try
        {
            var body = wh.Data;
            body[..28].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(body, DescMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(body[4..], DescVersion);
            body[6] = kind;
            if (kind == KindSegment && spec is { } s)
            {
                body[7] = (byte)s.Kind;
                BinaryPrimitives.WriteInt32LittleEndian(body[8..], s.PropertyKeyId);
                BinaryPrimitives.WriteInt64LittleEndian(body[12..], s.DefaultRaw);
            }
        }
        finally { wh.Dispose(); }
    }

    /// <summary>
    /// DataTenant の記述子を読む。テナントが空 (placeholder 未書込) なら <see cref="KindNone"/>。
    /// </summary>
    public static (byte Kind, PayloadLaneSpec? Spec) ReadDescriptor(IPagedFile data)
    {
        if (data.PageCount < 2) return (KindNone, null);
        var rh = data.PinForRead(new PageId(1));
        try
        {
            var body = rh.Data;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(body);
            if (magic != DescMagic) return (KindNone, null);
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            if (version != DescVersion) return (KindNone, null);
            byte kind = body[6];
            if (kind == KindSegment)
            {
                var payloadKind = (PayloadKind)body[7];
                int propKey = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
                long defaultRaw = BinaryPrimitives.ReadInt64LittleEndian(body[12..]);
                return (kind, new PayloadLaneSpec(payloadKind, propKey, defaultRaw));
            }
            return (KindNone, null);
        }
        finally { rh.Dispose(); }
    }

    // ──────────────────────────────────────────────────────────────────
    // IndexTenant (VertexId → 先頭ブロック論理 PageId)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 索引テナントを <paramref name="firstPageIds"/> でゼロから書き直す。
    /// 論理 page 1 = entryCount、論理 page 2+ = int64 エントリ列。
    /// </summary>
    public static void WriteIndex(IPagedFile idx, IReadOnlyList<long> firstPageIds)
    {
        idx.Truncate(1); // 既存ページをグローバル free list へ回収して作り直す
        long entryCount = firstPageIds.Count;
        int dataPages = (int)((entryCount + IndexEntriesPerPage - 1) / IndexEntriesPerPage);

        // header (論理 1) + data (論理 2..) を確保
        while (idx.PageCount < 2 + dataPages) idx.AllocatePage(PageKind.Header);

        var hh = idx.PinForWrite(IndexHeaderPage);
        try
        {
            hh.Data[..8].Clear();
            BinaryPrimitives.WriteInt64LittleEndian(hh.Data, entryCount);
        }
        finally { hh.Dispose(); }

        long vertex = 0;
        for (int page = 0; page < dataPages; page++)
        {
            var dh = idx.PinForWrite(new PageId(2 + page));
            try
            {
                var body = dh.Data;
                int slots = (int)Math.Min(IndexEntriesPerPage, entryCount - vertex);
                for (int s = 0; s < slots; s++, vertex++)
                    BinaryPrimitives.WriteInt64LittleEndian(body[(s * 8)..], firstPageIds[(int)vertex]);
            }
            finally { dh.Dispose(); }
        }
    }

    /// <summary>索引テナントの entryCount (= bulk load 時の vertexHwm) を読む。空なら 0。</summary>
    public static long ReadIndexEntryCount(IPagedFile idx)
    {
        if (idx.PageCount < 2) return 0;
        var hh = idx.PinForRead(IndexHeaderPage);
        try { return BinaryPrimitives.ReadInt64LittleEndian(hh.Data); }
        finally { hh.Dispose(); }
    }

    /// <summary>
    /// VertexId <paramref name="vertexId"/> の先頭ブロック論理 PageId を返す (未索引 / 範囲外は -1)。
    /// <paramref name="entryCount"/> はコンストラクション時に <see cref="ReadIndexEntryCount"/> で取得した値。
    /// </summary>
    public static long ReadIndexEntry(IPagedFile idx, long entryCount, long vertexId)
    {
        if (vertexId < 0 || vertexId >= entryCount) return -1;
        long page = 2 + vertexId / IndexEntriesPerPage;
        int slot = (int)(vertexId % IndexEntriesPerPage);
        var rh = idx.PinForRead(new PageId(page));
        try { return BinaryPrimitives.ReadInt64LittleEndian(rh.Data[(slot * 8)..]); }
        finally { rh.Dispose(); }
    }
}
