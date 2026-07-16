using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

// v3 (MVCC sidecar) record レイアウト (48 バイト):
//  0 Flags(1) | 1 Source(6) | 7 Target(6) | 13 TypeId(2) |
// 15 SrcPrev(6) | 21 SrcNext(6) | 27 TgtPrev(6) | 33 TgtNext(6) | 39 FirstPropId(6) | 45 Pad(3)
//
// Xmin / Xmax は record から撤去し、EdgeVersionMeta sidecar に
// localId (= EdgeId.Value) をキーとして移管した。
//   Create: sidecar.Write(id, { Xmin = MvccContext.CurrentTxId, Xmax = 0 })
//   Delete (論理): sidecar.UpdateXmax(id, MvccContext.CurrentTxId)
//     チェーン (SrcPrev/SrcNext/TgtPrev/TgtNext) は unlink せず、slot も free list に戻さない。
//     これにより snapshot reader (xmax コミット以前にスナップショットを取った tx) が
//     依然として元の record を辿れる。物理回収は vacuum 担当。
internal sealed class EdgeStore : IEdgeStore
{
    public const int RecordSize = 48;
    private const byte FlagInUse = 0x01;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;       // int64
    private const int MetaHwm = 8;            // int64
    private const int MetaInUse = 16;         // int64
    private const int MetaFormatVersion = 31; // byte

    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 170

    private readonly IPagedFile _file;
    private readonly IEntityVersionStore _versions;
    private long _freeHead;
    private long _hwm;
    private long _inUseCount;

    public EdgeStore(IPagedFile file) : this(file, versions: null) { }

    public EdgeStore(IPagedFile file, IEntityVersionStore? versions)
    {
        _file = file;
        _versions = versions ?? new InMemoryEntityVersionStore();
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _freeHead = -1; _hwm = 0; _inUseCount = 0;
            FlushMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
        }
    }

    public long InUseCount => _inUseCount;

    public EdgeId Create(IVertexStore vertexStore, VertexId source, VertexId target, EdgeTypeId type)
    {
        // raw adjacency entry が Sequence だけを保持している間は、回収 slot を再利用すると
        // 旧 entry が新しい edge へ付け替わる。再利用解放 coordinator が全 derived
        // entry の再構築を保証するまでは high-water mark からだけ割り当てる。
        long id = _hwm++;
        long generation = _versions.Read(id).Generation + 1;
        _inUseCount++;
        var edgeId = EdgeId.Create(id, checked((int)generation));

        EdgeId srcHead = vertexStore is VersionedVertexStore ns
            ? ns.GetFirstEdgeId(source)
            : ReadFirstEdgeId(vertexStore, source);
        EdgeId tgtHead = vertexStore is VersionedVertexStore ns2
            ? ns2.GetFirstEdgeId(target)
            : ReadFirstEdgeId(vertexStore, target);

        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        // オンディスク Int48 は Sequence (sentinel -1 は Sequence がそのまま返す)。
        RecordHelpers.WriteInt48(rec[1..], source.Sequence);
        RecordHelpers.WriteInt48(rec[7..], target.Sequence);
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)type.Value);
        RecordHelpers.WriteInt48(rec[15..], EdgeId.Invalid.Sequence);
        RecordHelpers.WriteInt48(rec[21..], srcHead.Sequence);
        RecordHelpers.WriteInt48(rec[27..], EdgeId.Invalid.Sequence);
        RecordHelpers.WriteInt48(rec[33..], tgtHead.Sequence);
        RecordHelpers.WriteInt48(rec[39..], PropertyId.Invalid.Sequence);
        _file.UnpinDirty(wpid, 0);
        // xmin/xmax と logical identity の Generation は同じ sidecar entry に書く。
        _versions.Write(id, new EntityVersionMeta(
            MvccContext.CurrentTxId.Value, 0, 0, long.MaxValue, generation));

        // 旧 head の物理 SrcPrev/TgtPrev を新 edge に向ける。MVCC でも prev pointer は
        // 「双方向リンクの維持」のために物理的に更新する (visibility 判定は xmin/xmax で行う)。
        if (srcHead.IsValid)
            UpdateListPrev(srcHead, source, edgeId);
        if (tgtHead.IsValid && tgtHead != srcHead)
            UpdateListPrev(tgtHead, target, edgeId);

        if (vertexStore is VersionedVertexStore ns3)
        {
            ns3.UpdateFirstEdgeId(source, edgeId);
            ns3.UpdateFirstEdgeId(target, edgeId);
        }
        else
        {
            var wSrc = vertexStore.Write(source);
            wSrc.FirstEdgeId = edgeId;
            wSrc.Dispose();
            var wTgt = vertexStore.Write(target);
            wTgt.FirstEdgeId = edgeId;
            wTgt.Dispose();
        }

        FlushMeta();
        return edgeId;
    }

    public void Delete(IVertexStore vertexStore, EdgeId edgeId)
    {
        // MVCC: 論理削除のみ — xmax をスタンプ、チェーンや slot は維持する。
        // 物理回収と chain 整理は vacuum で行うが、再利用解放は行わない。
        // 関連: vertexStore.firstEdgeId は更新しない (snapshot reader が辿れるよう head 維持)。
        _ = vertexStore;
        // 論理削除は sidecar の xmax をスタンプするだけ。record 本体は触らない。
        _versions.UpdateXmax(edgeId.Sequence, MvccContext.CurrentTxId.Value); // version キーは Sequence

        _inUseCount--;
        FlushMeta();
    }

    public EdgeReadHandle Read(EdgeId edgeId)
    {
        // HWM 超 / 負 ID は "存在しない" 扱い。VertexStore.Read と同じ理由。
        // slot 演算 / version キーは Sequence (packed Value ではない)。
        long seq = edgeId.Sequence;
        if (seq < 0 || seq >= _hwm)
            return new EdgeReadHandle(
                edgeId, inUse: false, default, default, default,
                EdgeId.Invalid, EdgeId.Invalid,
                EdgeId.Invalid, EdgeId.Invalid,
                PropertyId.Invalid);
        var (pageId, off) = Location(seq);
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        bool inUse = (rec[0] & FlagInUse) != 0;
        EntityVersionMeta meta = _versions.Read(seq);
        if (edgeId.Generation != 0 && edgeId.Generation != meta.Generation)
            return new EdgeReadHandle(
                edgeId, inUse: false, default, default, default,
                EdgeId.Invalid, EdgeId.Invalid,
                EdgeId.Invalid, EdgeId.Invalid,
                PropertyId.Invalid);

        var src = new VertexId(RecordHelpers.ReadInt48(rec[1..]));
        var tgt = new VertexId(RecordHelpers.ReadInt48(rec[7..]));
        var type = new EdgeTypeId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..]));
        var srcPrev = new EdgeId(RecordHelpers.ReadInt48(rec[15..]));
        var srcNext = new EdgeId(RecordHelpers.ReadInt48(rec[21..]));
        var tgtPrev = new EdgeId(RecordHelpers.ReadInt48(rec[27..]));
        var tgtNext = new EdgeId(RecordHelpers.ReadInt48(rec[33..]));
        var firstPropId = new PropertyId(RecordHelpers.ReadInt48(rec[39..]));
        // xmin/xmax は sidecar から。物理 free スロットは sidecar を引かない。
        if (inUse)
        {
            if (!Visibility.IsVisibleAmbient(meta.Xmin, meta.Xmax))
                inUse = false;
        }
        // 可視な edge を観測したら SSN read-set に記録する (Serializable 時のみ)。
        // traversal の EdgeEnumerator もこの Read を通るので隣接走査が一律捕捉される。
        if (inUse) MvccContext.RecordRead(EntityKind.Edge, seq);
        var resolvedId = meta.Generation > 0
            ? EdgeId.Create(seq, checked((int)meta.Generation))
            : edgeId;
        return new EdgeReadHandle(resolvedId, inUse, src, tgt, type, srcPrev, srcNext, tgtPrev, tgtNext, firstPropId);
    }

    public EdgeWriteHandle Write(EdgeId edgeId)
    {
        var (pageId, off) = Location(edgeId.Sequence); // slot は Sequence
        var ph = _file.PinForWrite(pageId);
        return new EdgeWriteHandle(_file, pageId, ph.Data.Slice(off, RecordSize));
    }

    public EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore vertexStore)
    {
        EdgeId first = vertexStore is VersionedVertexStore ns
            ? ns.GetFirstEdgeId(vertexId)
            : GetFirstEdgeIdViaInterface(vertexStore, vertexId);
        return new EdgeEnumerator(this, vertexStore, vertexId, first);
    }

    public EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore vertexStore,
        EdgeTypeId type, Direction direction)
    {
        EdgeId first = vertexStore is VersionedVertexStore ns
            ? ns.GetFirstEdgeId(vertexId)
            : GetFirstEdgeIdViaInterface(vertexStore, vertexId);
        return new EdgeEnumerator(this, vertexStore, vertexId, first, type, direction);
    }

    public IEnumerable<EdgeId> Scan()
    {
        long hwm = _hwm;
        for (long id = 0; id < hwm; id++)
        {
            var (pageId, off) = Location(id);
            bool inUse;
            {
                using var h = _file.PinForRead(pageId);
                ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
                inUse = (rec[0] & FlagInUse) != 0;
            }
            if (!inUse) continue;
            var meta = _versions.Read(id);
            if (Visibility.IsVisibleAmbient(meta.Xmin, meta.Xmax))
            {
                // scan で観測した可視 edge も SSN read-set に記録する。
                MvccContext.RecordRead(EntityKind.Edge, id);
                yield return EdgeId.Create(id, checked((int)meta.Generation));
            }
        }
    }

    public int CurrentGeneration(long localId)
    {
        if (localId < 0 || localId >= _hwm) return -1;
        long generation = _versions.Read(localId).Generation;
        return generation <= 0 ? -1 : checked((int)generation);
    }

    // 旧 EdgeStore は inline property 非対応。すべて false を返し、property は
    // overflow チェーン (PropertyStore) に委ねる (graceful degrade)。production では未配線。
    public bool TryGetInlineProperty(EdgeId edgeId, PropertyKeyId keyId, out PropertyValue value) { value = default; return false; }
    public bool HasInlineProperty(EdgeId edgeId, PropertyKeyId keyId) => false;
    public bool SetInlineProperty(EdgeId edgeId, PropertyKeyId keyId, in PropertyValue value) => false;
    public bool RemoveInlineProperty(EdgeId edgeId, PropertyKeyId keyId) => false;
    public PropertyEnumerator EnumerateProperties(EdgeId edgeId, IPropertyStore overflowStore)
        => new PropertyEnumerator(overflowStore, Read(edgeId).FirstPropertyId);

    // --- internal bulk-load helpers (no per-record FlushMeta) ---

    internal void BulkWrite(long id, long src, long tgt, int typeId,
        long srcPrev, long srcNext, long tgtPrev, long tgtNext)
    {
        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        RecordHelpers.WriteInt48(rec[1..], src);
        RecordHelpers.WriteInt48(rec[7..], tgt);
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)typeId);
        RecordHelpers.WriteInt48(rec[15..], srcPrev);
        RecordHelpers.WriteInt48(rec[21..], srcNext);
        RecordHelpers.WriteInt48(rec[27..], tgtPrev);
        RecordHelpers.WriteInt48(rec[33..], tgtNext);
        RecordHelpers.WriteInt48(rec[39..], -1L);
        _file.UnpinDirty(wpid, 0);
        // bulk load は MvccContext が無いので Bootstrap を xmin に (sidecar)。
        _versions.Write(id, new EntityVersionMeta(
            TransactionId.Bootstrap.Value, 0, 0, long.MaxValue, 1));
    }

    internal void BulkSetHeaders(long hwm, long inUseCount)
    {
        _hwm = hwm;
        _inUseCount = inUseCount;
        _freeHead = -1;
        FlushMeta();
    }

    /// <summary>
    /// vacuum: Vertexストアと協調してリレーション双方向 chain を再構築し、
    /// dead version を物理回収する。実行手順:
    /// <list type="number">
    ///   <item>各 live Vertexの chain を walk → live edge だけで chain 再構築 (vertex.FirstEdgeId 更新含む)。
    ///         dead edge は reclaim 集合に追加。</item>
    ///   <item>残った edge slot を走査し、reclaim 集合に未登録の dead edge (両端 dead Vertexに繋がる、など)
    ///         を追加。</item>
    ///   <item>reclaim 集合の各 slot を物理回収する。Sequence は再利用可能にしない。</item>
    /// </list>
    /// 呼び出し前提: アクティブトランザクション 0 件、Vertex vacuum **前**。
    /// </summary>
    /// <returns>物理回収したEdge版数。</returns>
    internal int VacuumDeadVersions(VersionedVertexStore vertexStore, long horizonTxId, CommittedTxRegistry committed)
    {
        var reclaimSet = new HashSet<long>();

        // Pass 1: 各Vertex (live / dead 区別なく raw inUse=1) の chain を rebuild。
        // dead Vertexの chain 上の edge は全て dead 想定 (DeleteVertex の cascade による) → 全部 reclaim 集合へ。
        long vertexHwm = vertexStore.Hwm;
        for (long nid = 0; nid < vertexHwm; nid++)
        {
            var raw = vertexStore.ReadRaw(nid);
            if (!raw.InUse) continue;
            if (!raw.FirstEdgeId.IsValid) continue;

            bool vertexWillBeReclaimed = raw.Xmax != 0
                && raw.Xmax < horizonTxId
                && committed.IsCommitted(raw.Xmax);

            RebuildChainForVertex(new VertexId(nid), raw.FirstEdgeId, vertexStore,
                horizonTxId, committed, reclaimSet, vertexWillBeReclaimed);
        }

        // Pass 2: 全 edge slot を走査し、まだ reclaim 集合に居ない dead edge を拾う。
        for (long id = 0; id < _hwm; id++)
        {
            if (reclaimSet.Contains(id)) continue;
            var (pageId, off) = Location(id);
            using var h = _file.PinForRead(pageId);
            ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
            bool inUse = (rec[0] & FlagInUse) != 0;
            if (!inUse) continue;
            long xmax = _versions.Read(id).Xmax; // xmax は sidecar から
            bool dead = xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax);
            if (dead) reclaimSet.Add(id);
        }

        // Pass 3: reclaim 集合の slot を物理回収する。raw adjacency entry の再構築と
        // durable publish が揃うまでは free list へ release しない。
        foreach (var id in reclaimSet)
            ReclaimSlot(id);

        if (reclaimSet.Count > 0)
        {
            FlushMeta();
        }
        return reclaimSet.Count;
    }

    private void RebuildChainForVertex(VertexId vertex, EdgeId head,
        VersionedVertexStore vertexStore, long horizonTxId, CommittedTxRegistry committed,
        HashSet<long> reclaimSet, bool vertexWillBeReclaimed)
    {
        // chain を遍歴して live edge 列を抜き出す。各 chain entry は (edgeId, side)。
        // side: このVertexが source か target か。
        var entries = new List<(EdgeId Id, bool VertexIsSource, bool Dead)>();
        var cur = head;
        long guard = _hwm + 1;
        while (cur.IsValid && guard-- > 0)
        {
            var (pageId, off) = Location(cur.Sequence); // slot は Sequence
            bool vertexIsSource;
            bool dead;
            EdgeId nextOnThisSide;
            using (var h = _file.PinForRead(pageId))
            {
                ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
                bool inUse = (rec[0] & FlagInUse) != 0;
                long src = RecordHelpers.ReadInt48(rec[1..]);
                long tgt = RecordHelpers.ReadInt48(rec[7..]);
                // オンディスク src/tgt は Sequence。vertex も Sequence で突き合わせる。
                vertexIsSource = src == vertex.Sequence;
                bool vertexIsTarget = tgt == vertex.Sequence;
                if (!vertexIsSource && !vertexIsTarget)
                {
                    // chain 整合性が崩れている (WAL リカバリで起きうる) → ここで打ち切る
                    break;
                }
                nextOnThisSide = vertexIsSource
                    ? new EdgeId(RecordHelpers.ReadInt48(rec[21..])) // srcNext
                    : new EdgeId(RecordHelpers.ReadInt48(rec[33..])); // tgtNext
                if (!inUse)
                {
                    dead = true;
                }
                else
                {
                    long xmax = _versions.Read(cur.Sequence).Xmax; // xmax は sidecar から
                    dead = xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax);
                }
            }
            entries.Add((cur, vertexIsSource, dead));
            if (dead) reclaimSet.Add(cur.Sequence);
            cur = nextOnThisSide;
        }

        if (vertexWillBeReclaimed)
        {
            // Vertex自体が消えるので chain head 更新は不要。
            // dead だけでなく live (xmax=0) もこのVertexからは参照不能になる:
            // - 相手Vertexが live なら相手の chain rebuild で扱われる (= live 維持)。
            // - 相手Vertexも dead なら、reclaim 集合に入る (Pass 2 で拾われる)。
            // 何もしなくて良い。
            return;
        }

        // live Vertex: live entries だけ残して chain を再構築。
        EdgeId newHead = EdgeId.Invalid;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (id, vertexIsSource, dead) = entries[i];
            if (dead) continue;
            // この edge の "vertex-side" prev/next を再リンク。
            // newHead (= 後続) と、後で前任者が書き込む prev=Invalid を初期値にしておく。
            var (pageId, off) = Location(id.Sequence);
            var ph = _file.PinForWrite(pageId);
            // vertexIsSource ? srcPrev=15/srcNext=21 : tgtPrev=27/tgtNext=33
            int prevOff = vertexIsSource ? 15 : 27;
            int nextOff = vertexIsSource ? 21 : 33;
            RecordHelpers.WriteInt48(ph.Data[(off + prevOff)..], EdgeId.Invalid.Sequence);
            RecordHelpers.WriteInt48(ph.Data[(off + nextOff)..], newHead.Sequence);
            _file.UnpinDirty(pageId, 0);

            if (newHead.IsValid)
            {
                // newHead 側の prev を id にする。newHead の source/target どちらが vertex かは
                // 後続 (前に処理した) entry の vertexIsSource フラグで判明している。
                // entries[i+1...] の中で最初の live のものが newHead だが、ここでは順序逆走で
                // 一つ前の live を覚えておけば良い → リファクタする。
                // 簡略のため後続パスで再走する。
            }
            newHead = id;
        }

        // newHead の前任者 (= newHead 自身の prev) は Invalid のままで、後続の prev は前任者を指す。
        // 上のループでは prev を Invalid に固定したので、もう一度走って各 live の prev を正しく書く。
        EdgeId prev = EdgeId.Invalid;
        foreach (var (id, vertexIsSource, dead) in entries)
        {
            if (dead) continue;
            var (pageId, off) = Location(id.Sequence);
            var ph = _file.PinForWrite(pageId);
            int prevOff = vertexIsSource ? 15 : 27;
            RecordHelpers.WriteInt48(ph.Data[(off + prevOff)..], prev.Sequence);
            _file.UnpinDirty(pageId, 0);
            prev = id;
        }

        // Vertexの FirstEdgeId を更新。
        vertexStore.UpdateFirstEdgeIdRaw(vertex, newHead);
    }

    private void ReclaimSlot(long id)
    {
        var (pageId, off) = Location(id);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        rec.Clear();
        _file.UnpinDirty(pageId, 0);
    }

    /// <summary>テスト用。現在の HWM スロット数 (free 含む)。</summary>
    internal long Hwm => _hwm;

    /// <summary>テスト用。free list 先頭 (-1 で空)。</summary>
    internal long FreeHead => _freeHead;

    /// <summary>現在の <c>_hwm</c> を保持するのに必要な最小ページ数 (meta + header + record pages)。</summary>
    internal long ComputeRequiredPageCount()
        => _hwm == 0 ? 2L : ((_hwm - 1) / RecordsPerPage) + 3L;

    /// <summary>内部 PagedFile への参照 (vacuum/truncate 経路で使用)。</summary>
    internal IPagedFile UnderlyingFile => _file;

    /// <summary>
    /// ヘッダページからインメモリのメタ (hwm / freeHead / inUseCount) を読み直す。
    /// abort の before-image 巻き戻し後、およびクラッシュ recovery 後に呼ばれ、
    /// ページバックされたメタとインメモリのキャッシュを同期する。
    /// </summary>
    internal void ReloadMeta() => LoadMeta();

    // --- private helpers ---

    private void UpdateListPrev(EdgeId edgeId, VertexId side, EdgeId newPrev)
    {
        var (pageId, off) = Location(edgeId.Sequence);
        var ph = _file.PinForWrite(pageId);
        ReadOnlySpan<byte> snap = ph.Data.Slice(off, RecordSize);
        VertexId recSrc = new(RecordHelpers.ReadInt48(snap[1..])); // gen=0、equality は Sequence ベース
        int prevOff = recSrc == side ? off + 15 : off + 27;
        RecordHelpers.WriteInt48(ph.Data[prevOff..], newPrev.Sequence);
        _file.UnpinDirty(pageId, 0);
    }

    private static EdgeId ReadFirstEdgeId(IVertexStore vertexStore, VertexId vertexId)
    {
        using var h = vertexStore.Read(vertexId);
        return h.FirstEdgeId;
    }

    private static EdgeId GetFirstEdgeIdViaInterface(IVertexStore vertexStore, VertexId vertexId)
    {
        using var h = vertexStore.Read(vertexId);
        return h.FirstEdgeId;
    }

    private (PageId pageId, int offset) Location(long id)
    {
        int rpp = RecordsPerPage;
        return (new PageId(id / rpp + 2), (int)(id % rpp) * RecordSize);
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.EdgeRecord);
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _freeHead = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaFreeHead..]);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
        _inUseCount = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaInUse..]);
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != StorageFormatVersion.Current)
            throw new StorageFormatMismatchException("edges", v, StorageFormatVersion.Current);
    }

    private void FlushMeta(bool initialise = false)
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaInUse..], _inUseCount);
        if (initialise)
            ph.Data[MetaFormatVersion] = StorageFormatVersion.Current;
        _file.UnpinDirty(HeaderPageId, 0);
    }
}
