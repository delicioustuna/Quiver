using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

// FT-32 v3 (MVCC sidecar) record layout (48 バイト):
//  0 Flags(1) | 1 Source(6) | 7 Target(6) | 13 TypeId(2) |
// 15 SrcPrev(6) | 21 SrcNext(6) | 27 TgtPrev(6) | 33 TgtNext(6) | 39 FirstPropId(6) | 45 Pad(3)
//
// Xmin / Xmax は record から撤去し、RelationshipVersionMeta sidecar に
// localId (= RelationshipId.Value) をキーとして移管した。
//   Create: sidecar.Write(id, { Xmin = MvccContext.CurrentTxId, Xmax = 0 })
//   Delete (論理): sidecar.UpdateXmax(id, MvccContext.CurrentTxId)
//     チェーン (SrcPrev/SrcNext/TgtPrev/TgtNext) は unlink せず、slot も free list に戻さない。
//     これにより snapshot reader (xmax コミット以前にスナップショットを取った tx) が
//     依然として元の record を辿れる。物理回収は vacuum (OP-3) 担当。
internal sealed class RelationshipStore : IRelationshipStore
{
    public const int RecordSize = 48;
    private const byte FlagInUse = 0x01;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;       // int64
    private const int MetaHwm = 8;            // int64
    private const int MetaInUse = 16;         // int64
    private const int MetaFormatVersion = 31; // byte (FT-26)

    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 170

    private readonly IPagedFile _file;
    private readonly IEntityVersionStore _versions;
    private long _freeHead;
    private long _hwm;
    private long _inUseCount;

    public RelationshipStore(IPagedFile file) : this(file, versions: null) { }

    public RelationshipStore(IPagedFile file, IEntityVersionStore? versions)
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

    public RelationshipId Create(INodeStore nodeStore, NodeId source, NodeId target, RelationshipTypeId type)
    {
        // FT-26 MVCC: 論理削除に伴う slot 非再利用で free list は空のまま hwm 単調増加 (vacuum 完了後のみ free 投入)。
        long id;
        if (_freeHead >= 0)
        {
            id = _freeHead;
            var (fpid, foff) = Location(id);
            using var fh = _file.PinForRead(fpid);
            _freeHead = RecordHelpers.ReadInt48(fh.Data[(foff + 1)..]);
        }
        else
        {
            id = _hwm++;
        }
        _inUseCount++;
        var relId = new RelationshipId(id);

        RelationshipId srcHead = nodeStore is VersionedNodeStore ns
            ? ns.GetFirstRelId(source)
            : ReadFirstRelId(nodeStore, source);
        RelationshipId tgtHead = nodeStore is VersionedNodeStore ns2
            ? ns2.GetFirstRelId(target)
            : ReadFirstRelId(nodeStore, target);

        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        // ARCH-5b: オンディスク Int48 は Sequence (sentinel -1 は Sequence がそのまま返す)。
        RecordHelpers.WriteInt48(rec[1..], source.Sequence);
        RecordHelpers.WriteInt48(rec[7..], target.Sequence);
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)type.Value);
        RecordHelpers.WriteInt48(rec[15..], RelationshipId.Invalid.Sequence);
        RecordHelpers.WriteInt48(rec[21..], srcHead.Sequence);
        RecordHelpers.WriteInt48(rec[27..], RelationshipId.Invalid.Sequence);
        RecordHelpers.WriteInt48(rec[33..], tgtHead.Sequence);
        RecordHelpers.WriteInt48(rec[39..], PropertyId.Invalid.Sequence);
        _file.UnpinDirty(wpid, 0);
        // FT-32: xmin/xmax は sidecar に書く。
        _versions.Write(id, new EntityVersionMeta(MvccContext.CurrentTxId.Value, 0, 0, long.MaxValue));

        // 旧 head の物理 SrcPrev/TgtPrev を新 rel に向ける。MVCC でも prev pointer は
        // 「双方向リンクの維持」のために物理的に更新する (visibility 判定は xmin/xmax で行う)。
        if (srcHead.IsValid)
            UpdateListPrev(srcHead, source, relId);
        if (tgtHead.IsValid && tgtHead != srcHead)
            UpdateListPrev(tgtHead, target, relId);

        if (nodeStore is VersionedNodeStore ns3)
        {
            ns3.UpdateFirstRelId(source, relId);
            ns3.UpdateFirstRelId(target, relId);
        }
        else
        {
            var wSrc = nodeStore.Write(source);
            wSrc.FirstRelationshipId = relId;
            wSrc.Dispose();
            var wTgt = nodeStore.Write(target);
            wTgt.FirstRelationshipId = relId;
            wTgt.Dispose();
        }

        FlushMeta();
        return relId;
    }

    public void Delete(INodeStore nodeStore, RelationshipId relId)
    {
        // FT-26 MVCC: 論理削除のみ — xmax をスタンプ、チェーンや slot は維持する。
        // 物理回収 + chain 整理 + free list 投入は vacuum (OP-3) で行う。
        // 関連: nodeStore.firstRelId は更新しない (snapshot reader が辿れるよう head 維持)。
        _ = nodeStore;
        // FT-32: 論理削除は sidecar の xmax をスタンプするだけ。record 本体は触らない。
        _versions.UpdateXmax(relId.Sequence, MvccContext.CurrentTxId.Value); // ARCH-5b: version キーは Sequence

        _inUseCount--;
        FlushMeta();
    }

    public RelationshipReadHandle Read(RelationshipId relId)
    {
        // FT-30: HWM 超 / 負 ID は "存在しない" 扱い。NodeStore.Read と同じ理由。
        // ARCH-5b: slot 演算 / version キーは Sequence (packed Value ではない)。
        long seq = relId.Sequence;
        if (seq < 0 || seq >= _hwm)
            return new RelationshipReadHandle(
                relId, inUse: false, default, default, default,
                RelationshipId.Invalid, RelationshipId.Invalid,
                RelationshipId.Invalid, RelationshipId.Invalid,
                PropertyId.Invalid);
        var (pageId, off) = Location(seq);
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        bool inUse = (rec[0] & FlagInUse) != 0;
        var src = new NodeId(RecordHelpers.ReadInt48(rec[1..]));
        var tgt = new NodeId(RecordHelpers.ReadInt48(rec[7..]));
        var type = new RelationshipTypeId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..]));
        var srcPrev = new RelationshipId(RecordHelpers.ReadInt48(rec[15..]));
        var srcNext = new RelationshipId(RecordHelpers.ReadInt48(rec[21..]));
        var tgtPrev = new RelationshipId(RecordHelpers.ReadInt48(rec[27..]));
        var tgtNext = new RelationshipId(RecordHelpers.ReadInt48(rec[33..]));
        var firstPropId = new PropertyId(RecordHelpers.ReadInt48(rec[39..]));
        // FT-32: xmin/xmax は sidecar から。物理 free スロットは sidecar を引かない。
        if (inUse)
        {
            var meta = _versions.Read(seq);
            if (!Visibility.IsVisibleAmbient(meta.Xmin, meta.Xmax))
                inUse = false;
        }
        // FT-33: 可視な relationship を観測したら SSN read-set に記録する (Serializable 時のみ)。
        // traversal の RelationshipEnumerator もこの Read を通るので隣接走査が一律捕捉される。
        if (inUse) MvccContext.RecordRead(EntityKind.Relationship, seq);
        return new RelationshipReadHandle(relId, inUse, src, tgt, type, srcPrev, srcNext, tgtPrev, tgtNext, firstPropId);
    }

    public RelationshipWriteHandle Write(RelationshipId relId)
    {
        var (pageId, off) = Location(relId.Sequence); // ARCH-5b: slot は Sequence
        var ph = _file.PinForWrite(pageId);
        return new RelationshipWriteHandle(_file, pageId, ph.Data.Slice(off, RecordSize));
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore)
    {
        RelationshipId first = nodeStore is VersionedNodeStore ns
            ? ns.GetFirstRelId(nodeId)
            : GetFirstRelIdViaInterface(nodeStore, nodeId);
        return new RelationshipEnumerator(this, nodeId, first);
    }

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore nodeStore,
        RelationshipTypeId type, Direction direction)
    {
        RelationshipId first = nodeStore is VersionedNodeStore ns
            ? ns.GetFirstRelId(nodeId)
            : GetFirstRelIdViaInterface(nodeStore, nodeId);
        return new RelationshipEnumerator(this, nodeId, first, type, direction);
    }

    public IEnumerable<RelationshipId> Scan()
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
                // FT-33: scan で観測した可視 relationship も SSN read-set に記録する。
                MvccContext.RecordRead(EntityKind.Relationship, id);
                yield return new RelationshipId(id);
            }
        }
    }

    // ARCH-5c Phase 4: 旧 RelationshipStore は inline property 非対応。すべて false を返し、property は
    // overflow チェーン (PropertyStore) に委ねる (graceful degrade)。production では未配線。
    public bool TryGetInlineProperty(RelationshipId relId, PropertyKeyId keyId, out PropertyValue value) { value = default; return false; }
    public bool HasInlineProperty(RelationshipId relId, PropertyKeyId keyId) => false;
    public bool SetInlineProperty(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value) => false;
    public bool RemoveInlineProperty(RelationshipId relId, PropertyKeyId keyId) => false;
    public PropertyEnumerator EnumerateProperties(RelationshipId relId, IPropertyStore overflowStore)
        => new PropertyEnumerator(overflowStore, Read(relId).FirstPropertyId);

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
        // FT-26/FT-32: bulk load は MvccContext が無いので Bootstrap を xmin に (sidecar)。
        _versions.Write(id, new EntityVersionMeta(TransactionId.Bootstrap.Value, 0, 0, long.MaxValue));
    }

    internal void BulkSetHeaders(long hwm, long inUseCount)
    {
        _hwm = hwm;
        _inUseCount = inUseCount;
        _freeHead = -1;
        FlushMeta();
    }

    /// <summary>
    /// OP-3 vacuum: ノードストアと協調してリレーション双方向 chain を再構築し、
    /// dead version を物理回収する。実行手順:
    /// <list type="number">
    ///   <item>各 live ノードの chain を walk → live rel だけで chain 再構築 (node.FirstRelId 更新含む)。
    ///         dead rel は reclaim 集合に追加。</item>
    ///   <item>残った rel slot を走査し、reclaim 集合に未登録の dead rel (両端 dead ノードに繋がる、など)
    ///         を追加。</item>
    ///   <item>reclaim 集合の各 slot を物理 free (clear + free list 投入)。</item>
    /// </list>
    /// 呼び出し前提: アクティブトランザクション 0 件、ノード vacuum **前**。
    /// </summary>
    /// <returns>物理回収したリレーションシップ版数。</returns>
    internal int VacuumDeadVersions(VersionedNodeStore nodeStore, long horizonTxId, CommittedTxRegistry committed)
    {
        var reclaimSet = new HashSet<long>();

        // Pass 1: 各ノード (live / dead 区別なく raw inUse=1) の chain を rebuild。
        // dead ノードの chain 上の rel は全て dead 想定 (DeleteNode の cascade による) → 全部 reclaim 集合へ。
        long nodeHwm = nodeStore.Hwm;
        for (long nid = 0; nid < nodeHwm; nid++)
        {
            var raw = nodeStore.ReadRaw(nid);
            if (!raw.InUse) continue;
            if (!raw.FirstRelId.IsValid) continue;

            bool nodeWillBeReclaimed = raw.Xmax != 0
                && raw.Xmax < horizonTxId
                && committed.IsCommitted(raw.Xmax);

            RebuildChainForNode(new NodeId(nid), raw.FirstRelId, nodeStore,
                horizonTxId, committed, reclaimSet, nodeWillBeReclaimed);
        }

        // Pass 2: 全 rel slot を走査し、まだ reclaim 集合に居ない dead rel を拾う。
        for (long id = 0; id < _hwm; id++)
        {
            if (reclaimSet.Contains(id)) continue;
            var (pageId, off) = Location(id);
            using var h = _file.PinForRead(pageId);
            ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
            bool inUse = (rec[0] & FlagInUse) != 0;
            if (!inUse) continue;
            long xmax = _versions.Read(id).Xmax; // FT-32: xmax は sidecar から
            bool dead = xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax);
            if (dead) reclaimSet.Add(id);
        }

        // Pass 3: reclaim 集合の slot を物理 free。
        foreach (var id in reclaimSet)
            ReclaimSlot(id);

        if (reclaimSet.Count > 0)
        {
            ShrinkHwmFromTrailingFreeSlots();
            FlushMeta();
        }
        return reclaimSet.Count;
    }

    private void RebuildChainForNode(NodeId node, RelationshipId head,
        VersionedNodeStore nodeStore, long horizonTxId, CommittedTxRegistry committed,
        HashSet<long> reclaimSet, bool nodeWillBeReclaimed)
    {
        // chain を遍歴して live rel 列を抜き出す。各 chain entry は (relId, side)。
        // side: このノードが source か target か。
        var entries = new List<(RelationshipId Id, bool NodeIsSource, bool Dead)>();
        var cur = head;
        long guard = _hwm + 1;
        while (cur.IsValid && guard-- > 0)
        {
            var (pageId, off) = Location(cur.Sequence); // ARCH-5b: slot は Sequence
            bool nodeIsSource;
            bool dead;
            RelationshipId nextOnThisSide;
            using (var h = _file.PinForRead(pageId))
            {
                ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
                bool inUse = (rec[0] & FlagInUse) != 0;
                long src = RecordHelpers.ReadInt48(rec[1..]);
                long tgt = RecordHelpers.ReadInt48(rec[7..]);
                // オンディスク src/tgt は Sequence。node も Sequence で突き合わせる。
                nodeIsSource = src == node.Sequence;
                bool nodeIsTarget = tgt == node.Sequence;
                if (!nodeIsSource && !nodeIsTarget)
                {
                    // chain 整合性が崩れている (FT-15/17 のリカバリで起きうる) → ここで打ち切る
                    break;
                }
                nextOnThisSide = nodeIsSource
                    ? new RelationshipId(RecordHelpers.ReadInt48(rec[21..])) // srcNext
                    : new RelationshipId(RecordHelpers.ReadInt48(rec[33..])); // tgtNext
                if (!inUse)
                {
                    dead = true;
                }
                else
                {
                    long xmax = _versions.Read(cur.Sequence).Xmax; // FT-32: xmax は sidecar から
                    dead = xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax);
                }
            }
            entries.Add((cur, nodeIsSource, dead));
            if (dead) reclaimSet.Add(cur.Sequence);
            cur = nextOnThisSide;
        }

        if (nodeWillBeReclaimed)
        {
            // ノード自体が消えるので chain head 更新は不要。
            // dead だけでなく live (xmax=0) もこのノードからは参照不能になる:
            // - 相手ノードが live なら相手の chain rebuild で扱われる (= live 維持)。
            // - 相手ノードも dead なら、reclaim 集合に入る (Pass 2 で拾われる)。
            // 何もしなくて良い。
            return;
        }

        // live ノード: live entries だけ残して chain を再構築。
        RelationshipId newHead = RelationshipId.Invalid;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (id, nodeIsSource, dead) = entries[i];
            if (dead) continue;
            // この rel の "node-side" prev/next を再リンク。
            // newHead (= 後続) と、後で前任者が書き込む prev=Invalid を初期値にしておく。
            var (pageId, off) = Location(id.Sequence);
            var ph = _file.PinForWrite(pageId);
            // nodeIsSource ? srcPrev=15/srcNext=21 : tgtPrev=27/tgtNext=33
            int prevOff = nodeIsSource ? 15 : 27;
            int nextOff = nodeIsSource ? 21 : 33;
            RecordHelpers.WriteInt48(ph.Data[(off + prevOff)..], RelationshipId.Invalid.Sequence);
            RecordHelpers.WriteInt48(ph.Data[(off + nextOff)..], newHead.Sequence);
            _file.UnpinDirty(pageId, 0);

            if (newHead.IsValid)
            {
                // newHead 側の prev を id にする。newHead の source/target どちらが node かは
                // 後続 (前に処理した) entry の nodeIsSource フラグで判明している。
                // entries[i+1...] の中で最初の live のものが newHead だが、ここでは順序逆走で
                // 一つ前の live を覚えておけば良い → リファクタする。
                // 簡略のため後続パスで再走する。
            }
            newHead = id;
        }

        // newHead の前任者 (= newHead 自身の prev) は Invalid のままで、後続の prev は前任者を指す。
        // 上のループでは prev を Invalid に固定したので、もう一度走って各 live の prev を正しく書く。
        RelationshipId prev = RelationshipId.Invalid;
        foreach (var (id, nodeIsSource, dead) in entries)
        {
            if (dead) continue;
            var (pageId, off) = Location(id.Sequence);
            var ph = _file.PinForWrite(pageId);
            int prevOff = nodeIsSource ? 15 : 27;
            RecordHelpers.WriteInt48(ph.Data[(off + prevOff)..], prev.Sequence);
            _file.UnpinDirty(pageId, 0);
            prev = id;
        }

        // ノードの FirstRelId を更新。
        nodeStore.UpdateFirstRelIdRaw(node, newHead);
    }

    private void ReclaimSlot(long id)
    {
        var (pageId, off) = Location(id);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        rec.Clear();
        // free list ポインタは byte[1..7] (48-bit)。NodeStore と同じ。
        RecordHelpers.WriteInt48(rec[1..], _freeHead);
        _file.UnpinDirty(pageId, 0);
        _freeHead = id;
    }

    private void ShrinkHwmFromTrailingFreeSlots()
    {
        long oldHwm = _hwm;
        long newHwm = oldHwm;
        while (newHwm > 0)
        {
            long candidate = newHwm - 1;
            var (pageId, off) = Location(candidate);
            using var h = _file.PinForRead(pageId);
            bool inUse = (h.Data[off] & FlagInUse) != 0;
            if (inUse) break;
            newHwm--;
        }
        if (newHwm == oldHwm) return;

        long head = _freeHead;
        var keep = new List<long>();
        long guard = oldHwm + 1;
        while (head >= 0 && guard-- > 0)
        {
            if (head < newHwm) keep.Add(head);
            var (pageId, off) = Location(head);
            using var h = _file.PinForRead(pageId);
            long next = RecordHelpers.ReadInt48(h.Data[(off + 1)..]);
            head = next;
        }
        long newHeadFree = -1;
        for (int i = keep.Count - 1; i >= 0; i--)
        {
            long id = keep[i];
            var (pageId, off) = Location(id);
            var ph = _file.PinForWrite(pageId);
            Span<byte> rec = ph.Data.Slice(off, RecordSize);
            RecordHelpers.WriteInt48(rec[1..], newHeadFree);
            _file.UnpinDirty(pageId, 0);
            newHeadFree = id;
        }
        _freeHead = newHeadFree;
        _hwm = newHwm;
    }

    /// <summary>OP-3 / テスト用。現在の HWM スロット数 (free 含む)。</summary>
    internal long Hwm => _hwm;

    /// <summary>OP-3 / テスト用。free list 先頭 (-1 で空)。</summary>
    internal long FreeHead => _freeHead;

    /// <summary>OP-5: 現在の <c>_hwm</c> を保持するのに必要な最小ページ数 (meta + header + record pages)。</summary>
    internal long ComputeRequiredPageCount()
        => _hwm == 0 ? 2L : ((_hwm - 1) / RecordsPerPage) + 3L;

    /// <summary>OP-5: 内部 PagedFile への参照 (vacuum/truncate 経路で使用)。</summary>
    internal IPagedFile UnderlyingFile => _file;

    /// <summary>
    /// FT-15: ヘッダページからインメモリのメタ (hwm / freeHead / inUseCount) を読み直す。
    /// abort の before-image 巻き戻し後、およびクラッシュ recovery 後に呼ばれ、
    /// ページバックされたメタとインメモリのキャッシュを同期する。
    /// </summary>
    internal void ReloadMeta() => LoadMeta();

    // --- private helpers ---

    private void UpdateListPrev(RelationshipId relId, NodeId side, RelationshipId newPrev)
    {
        var (pageId, off) = Location(relId.Sequence);
        var ph = _file.PinForWrite(pageId);
        ReadOnlySpan<byte> snap = ph.Data.Slice(off, RecordSize);
        NodeId recSrc = new(RecordHelpers.ReadInt48(snap[1..])); // gen=0、equality は Sequence ベース
        int prevOff = recSrc == side ? off + 15 : off + 27;
        RecordHelpers.WriteInt48(ph.Data[prevOff..], newPrev.Sequence);
        _file.UnpinDirty(pageId, 0);
    }

    private static RelationshipId ReadFirstRelId(INodeStore nodeStore, NodeId nodeId)
    {
        using var h = nodeStore.Read(nodeId);
        return h.FirstRelationshipId;
    }

    private static RelationshipId GetFirstRelIdViaInterface(INodeStore nodeStore, NodeId nodeId)
    {
        using var h = nodeStore.Read(nodeId);
        return h.FirstRelationshipId;
    }

    private (PageId pageId, int offset) Location(long id)
    {
        int rpp = RecordsPerPage;
        return (new PageId(id / rpp + 2), (int)(id % rpp) * RecordSize);
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.RelationshipRecord);
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
        if (v != FormatVersion.Current)
            throw new FormatVersionMismatchException("rels", v, FormatVersion.Current);
    }

    private void FlushMeta(bool initialise = false)
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaInUse..], _inUseCount);
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.Current;
        _file.UnpinDirty(HeaderPageId, 0);
    }
}
