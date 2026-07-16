using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>vacuum: 可視性フィルタを通さない raw Vertexレコード。</summary>
internal struct RawVertexRecord
{
    public bool InUse;
    public EdgeId FirstEdgeId;
    public PropertyId FirstPropId;
    public LabelId Label;
    public long Xmin;
    public long Xmax;
}

// v3 (MVCC sidecar) record layout (15 bytes):
//  0 Flags(1) | 1 FirstEdgeId(6) | 7 FirstPropId(6) | 13 LabelId(2)
//
// Xmin / Xmax は record から撤去し、EntityVersionMeta sidecar (VertexVersionMeta) に
// localId (= VertexId.Value) をキーとして移管した。
//   Allocate: sidecar.Write(id, { Xmin = MvccContext.CurrentTxId, Xmax = 0 })
//   Free (logical delete): sidecar.UpdateXmax(id, MvccContext.CurrentTxId)
//     チェーン / record 本体は保持 (snapshot reader が辿れるよう)、物理回収は vacuum 担当。
internal sealed class VertexStore : IVertexStore
{
    public const int RecordSize = 15;
    private const byte FlagInUse = 0x01;

    // PageId(0) = PagedFile meta; PageId(1) = VertexStore header; PageId(2+) = records
    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;       // int64
    private const int MetaHwm = 8;            // int64
    private const int MetaInUse = 16;         // int64
    private const int MetaFormatVersion = 31; // byte (QUIVER-SW family version sentinel)

    public static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 544

    private readonly IPagedFile _file;
    private readonly IEntityVersionStore _versions;
    private LabelVertexIndex? _labelIndex;
    private long _freeHead;
    private long _hwm;
    private long _inUseCount;

    public VertexStore(IPagedFile file) : this(file, labelIndex: null, versions: null) { }

    public VertexStore(IPagedFile file, LabelVertexIndex? labelIndex) : this(file, labelIndex, versions: null) { }

    public VertexStore(IPagedFile file, LabelVertexIndex? labelIndex, IEntityVersionStore? versions)
    {
        _file = file;
        _versions = versions ?? new InMemoryEntityVersionStore();
        _labelIndex = labelIndex;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header); // allocates PageId(1)
            _freeHead = -1; _hwm = 0; _inUseCount = 0;
            FlushMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
        }
    }

    public void AttachLabelIndex(LabelVertexIndex labelIndex) => _labelIndex = labelIndex;

    internal void ReloadMeta()
    {
        LoadMeta();
        _labelIndex?.Invalidate();
    }

    public long InUseCount => _inUseCount;

    public VertexId Allocate(LabelId labelId)
    {
        // 論理削除に伴うチェーン非解除で free list の slot を物理的に再利用しなくなる。
        // フリーリストは vacuum 完了時にのみエントリが入る。それまでは hwm 単調増加。
        // slot を再利用するたびに Generation を +1 する。世代は sidecar に残るため
        // (vacuum / hwm shrink は sidecar を消さない)、free を跨いで前回値を読んで継ぐ。
        long id = -1;
        while (_freeHead >= 0)
        {
            long candidate = _freeHead;
            var (fpid, foff) = Location(candidate);
            using (var fh = _file.PinForRead(fpid))
                _freeHead = RecordHelpers.ReadInt48(fh.Data[(foff + 1)..]);
            // 世代が上限に達した slot は再利用しない (free list から外して永久退役)。
            // wraparound で古い索引エントリの世代と衝突するのを防ぐ。
            if (_versions.Read(candidate).Generation >= EntityRef.MaxGeneration)
                continue;
            id = candidate;
            break;
        }
        if (id < 0)
            id = _hwm++;
        _inUseCount++;

        // hwm shrink で縮んだ slot が再び hwm 経由で割り当たる場合も sidecar に旧世代が残るため、
        // free / hwm のどちらの経路でも「現世代 + 1」を発番する (新規 slot は Unset→0→1)。
        long generation = _versions.Read(id).Generation + 1;

        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        RecordHelpers.WriteInt48(rec[1..], -1L);
        RecordHelpers.WriteInt48(rec[7..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)labelId.Value);
        _file.UnpinDirty(wpid, 0);

        // xmin/xmax は sidecar に書く。Pstamp=0 / Sstamp=MaxValue は SSN の既定。
        // Generation を同時に書き込む。
        _versions.Write(id, new EntityVersionMeta(MvccContext.CurrentTxId.Value, 0, 0, long.MaxValue, generation));

        FlushMeta();
        // 払い出す VertexId に世代を載せる。外部に往復したこの id は、後で slot が
        // 再利用 (free→vacuum→再 Allocate で gen+1) されると Read の世代照合で not-found になる。
        var newId = VertexId.Create(id, (int)generation);
        _labelIndex?.OnAllocate(newId, labelId);
        return newId;
    }

    public void Free(VertexId vertexId)
    {
        // MVCC: 論理削除のみ — xmax をスタンプして record / チェーンは維持する。
        // 物理回収 + free list 投入は vacuum 経路で行う。
        // slot 演算 / version キーは Sequence (packed Value ではない)。
        long seq = vertexId.Sequence;
        var (pageId, off) = Location(seq);
        LabelId prevLabel;
        {
            using var rh = _file.PinForRead(pageId);
            prevLabel = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rh.Data.Slice(off, RecordSize)[13..]));
        }
        // 論理削除は sidecar の xmax をスタンプするだけ。record 本体は触らない。
        _versions.UpdateXmax(seq, MvccContext.CurrentTxId.Value);

        _inUseCount--;
        FlushMeta();
        _labelIndex?.OnFree(vertexId, prevLabel);
    }

    public VertexReadHandle Read(VertexId vertexId)
    {
        // HWM を超える ID / 負の ID は "存在しない" 扱いで safe-return する。
        // これがないと PagedFile.PinForRead が未割当ページの magic=0 を検出して
        // CorruptionException を投げ、VertexExists / HasProperty 等の defensive read API が
        // false を返す契約を破ってしまう (OP-2 sample の GET /vertices/{id} で発覚)。
        // slot 演算 / version キーは Sequence (packed Value ではない)。
        long seq = vertexId.Sequence;
        if (seq < 0 || seq >= _hwm)
            return new VertexReadHandle(vertexId, inUse: false, EdgeId.Invalid, PropertyId.Invalid, default);
        var (pageId, off) = Location(seq);
        bool inUse;
        EdgeId firstEdge;
        PropertyId firstProp;
        LabelId label;
        {
            using var h = _file.PinForRead(pageId);
            ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
            inUse = (rec[0] & FlagInUse) != 0;
            firstEdge = new EdgeId(RecordHelpers.ReadInt48(rec[1..]));
            firstProp = new PropertyId(RecordHelpers.ReadInt48(rec[7..]));
            label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..]));
        }
        // xmin/xmax は sidecar から引く。物理 free スロット (inUse=false) は
        // sidecar を引かずに早期 return する (無駄な pin を避ける + stale sidecar を読まない)。
        long xmin = 0, xmax = 0;
        if (inUse)
        {
            var meta = _versions.Read(seq);
            xmin = meta.Xmin; xmax = meta.Xmax;
            // 呼出元が世代付き VertexId (= Allocate 由来) を持つ場合、現 slot 世代と照合し、
            // 不一致 (slot 再利用に伴う stale 参照) なら "存在しない" 扱いにする。世代 0 (= 内部
            // パイプライン / bulk / 旧来 new VertexId(seq)) は照合をスキップする。
            int carriedGen = vertexId.Generation;
            if (carriedGen != 0 && carriedGen != (int)meta.Generation)
                inUse = false;
            // ambient MVCC コンテキストで可視性をフィルタする。
            // 不可視なら InUse=false に縮退して呼出側に "存在しない" と見せる。
            else if (!Visibility.IsVisibleAmbient(xmin, xmax))
                inUse = false;
        }
        // 可視バージョンを観測したら SSN read-set に記録する (Serializable 時のみ。
        // 直接 Read だけでなく traversal の隣接走査もこの経路を通る)。
        if (inUse) MvccContext.RecordRead(EntityKind.Vertex, seq);
        return new VertexReadHandle(vertexId, inUse, firstEdge, firstProp, label, xmin, xmax);
    }

    public VertexWriteHandle Write(VertexId vertexId)
    {
        var (pageId, off) = Location(vertexId.Sequence); // slot は Sequence
        var ph = _file.PinForWrite(pageId);
        return new VertexWriteHandle(_file, pageId, ph.Data.Slice(off, RecordSize));
    }

    public IEnumerable<VertexId> Scan()
    {
        for (long id = 0; id < _hwm; id++)
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
                // scan で観測した可視Vertexも SSN read-set に記録する。
                MvccContext.RecordRead(EntityKind.Vertex, id);
                // クエリパイプラインは Sequence 空間 (gen=0) で実行する。世代は利用者境界
                // (QueryRow マテリアライズ) で load するため、scan は素の Sequence id を返す。
                yield return new VertexId(id);
            }
        }
    }

    // --- internal helpers (used by EdgeStore to update vertex's FirstEdgeId) ---

    internal void UpdateFirstEdgeId(VertexId vertexId, EdgeId newFirstEdgeId)
    {
        var (pageId, off) = Location(vertexId.Sequence);
        var ph = _file.PinForWrite(pageId);
        RecordHelpers.WriteInt48(ph.Data[(off + 1)..], newFirstEdgeId.Sequence); // Int48 は Sequence
        _file.UnpinDirty(pageId, 0);
    }

    internal EdgeId GetFirstEdgeId(VertexId vertexId)
    {
        var (pageId, off) = Location(vertexId.Sequence);
        using var h = _file.PinForRead(pageId);
        return new EdgeId(RecordHelpers.ReadInt48(h.Data[(off + 1)..]));
    }

    // --- internal bulk-load helpers (no per-record FlushMeta) ---

    internal void BulkWrite(long id, int labelId)
    {
        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();
        rec[0] = FlagInUse;
        RecordHelpers.WriteInt48(rec[1..], -1L);
        RecordHelpers.WriteInt48(rec[7..], -1L);
        BinaryPrimitives.WriteInt16LittleEndian(rec[13..], (short)labelId);
        _file.UnpinDirty(wpid, 0);
        // bulk load は MvccContext が無いことが多いため Bootstrap TxId を xmin に。
        // CommittedTxRegistry には常に Bootstrap が登録済みなので全 snapshot で可視。
        // bulk load は新規 slot 割当のみ (再利用しない) なので Generation = 1。
        _versions.Write(id, new EntityVersionMeta(TransactionId.Bootstrap.Value, 0, 0, long.MaxValue, 1));
    }

    /// <summary>
    /// slot <paramref name="localId"/> の現在の世代 (incarnation) を返す。索引値
    /// (<see cref="EntityRef"/>) の世代照合で stale 参照を弾くのに使う。範囲外 / 負は -1。
    /// MVCC 可視性は適用せず sidecar の世代だけを読む (= 索引の非可視フィルタ挙動を維持しつつ
    /// slot 再利用のみ検出する)。物理 free な slot も sidecar には旧世代が残るが、その slot を
    /// 指す古い索引エントリは「同世代」で一致し得る — 呼び出し側の後続 Read が InUse=false で
    /// 弾くため、現挙動 (defensive read) と等価。
    /// </summary>
    public int CurrentGeneration(long localId)
    {
        if (localId < 0) return -1;
        long gen = _versions.Read(localId).Generation;
        return gen > int.MaxValue ? int.MaxValue : (int)gen;
    }

    // 旧 VertexStore は inline property 非対応。すべて false を返し、property は
    // overflow チェーン (PropertyStore) に委ねる (graceful degrade)。production では未配線。
    public bool TryGetInlineProperty(VertexId vertexId, PropertyKeyId keyId, out PropertyValue value) { value = default; return false; }
    public bool HasInlineProperty(VertexId vertexId, PropertyKeyId keyId) => false;
    public bool SetInlineProperty(VertexId vertexId, PropertyKeyId keyId, in PropertyValue value) => false;
    public bool RemoveInlineProperty(VertexId vertexId, PropertyKeyId keyId) => false;
    public PropertyEnumerator EnumerateProperties(VertexId vertexId, IPropertyStore overflowStore)
        => new PropertyEnumerator(overflowStore, Read(vertexId).FirstPropertyId);

    internal void BulkUpdateFirstProp(long id, long firstPropId)
    {
        var (pageId, off) = Location(id);
        var ph = _file.PinForWrite(pageId);
        RecordHelpers.WriteInt48(ph.Data[(off + 7)..], firstPropId);
        _file.UnpinDirty(pageId, 0);
    }

    internal void BulkSetHeaders(long hwm, long inUseCount)
    {
        _hwm = hwm;
        _inUseCount = inUseCount;
        _freeHead = -1;
        FlushMeta();
        _labelIndex?.Invalidate();
    }

    /// <summary>
    /// vacuum: <paramref name="horizonTxId"/> 未満で xmax がコミット済みな dead version を
    /// 物理回収し、slot を free list に投入する。inUseCount は <see cref="Free"/> 時に既に
    /// 減算されているので触らない。<paramref name="committed"/> は xmax のコミット判定に使う
    /// registry。末尾の連続 free slot が hwm を下げられる場合は hwm も縮める。
    /// </summary>
    /// <remarks>
    /// 呼び出し側 (<c>Vacuum.Run</c>) はアクティブトランザクションが 0 の前提を保証する。
    /// ラベル索引は <see cref="Free"/> 時点で既に <c>OnFree</c> 済みなので触らない。
    /// </remarks>
    /// <returns>物理回収した dead version 数。</returns>
    internal int VacuumDeadVersions(long horizonTxId, CommittedTxRegistry committed)
    {
        int reclaimed = 0;
        for (long id = 0; id < _hwm; id++)
        {
            var (pageId, off) = Location(id);
            bool reclaimThis;
            {
                using var h = _file.PinForRead(pageId);
                ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
                bool inUse = (rec[0] & FlagInUse) != 0;
                if (!inUse) { continue; } // 既に物理 free
                long xmax = _versions.Read(id).Xmax; // xmax は sidecar から
                reclaimThis = xmax != 0
                    && xmax < horizonTxId
                    && committed.IsCommitted(xmax);
            }
            if (!reclaimThis) continue;

            var ph = _file.PinForWrite(pageId);
            Span<byte> rec2 = ph.Data.Slice(off, RecordSize);
            rec2.Clear();
            // free list link: byte[1..7] に prev head (48-bit signed)。
            RecordHelpers.WriteInt48(rec2[1..], _freeHead);
            _file.UnpinDirty(pageId, 0);
            _freeHead = id;
            reclaimed++;
        }

        if (reclaimed > 0)
        {
            ShrinkHwmFromTrailingFreeSlots();
            FlushMeta();
        }
        return reclaimed;
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
            ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
            bool inUse = (rec[0] & FlagInUse) != 0;
            if (inUse) break;
            newHwm--;
        }
        if (newHwm == oldHwm) return;

        // 末尾 slot を hwm 範囲外に出すので、free list 内の対象 slot を取り除き、繋ぎ直す。
        long head = _freeHead;
        var keep = new List<long>();
        while (head >= 0)
        {
            if (head < newHwm) keep.Add(head);
            var (pageId, off) = Location(head);
            using var h = _file.PinForRead(pageId);
            long next = RecordHelpers.ReadInt48(h.Data[(off + 1)..]);
            head = next;
        }
        long newHead = -1;
        for (int i = keep.Count - 1; i >= 0; i--)
        {
            long id = keep[i];
            var (pageId, off) = Location(id);
            var ph = _file.PinForWrite(pageId);
            Span<byte> rec = ph.Data.Slice(off, RecordSize);
            RecordHelpers.WriteInt48(rec[1..], newHead);
            _file.UnpinDirty(pageId, 0);
            newHead = id;
        }
        _freeHead = newHead;
        _hwm = newHwm;
    }

    /// <summary>テスト用。現在の HWM スロット数 (free 含む)。</summary>
    internal long Hwm => _hwm;

    /// <summary>
    /// 現在の <c>_hwm</c> を保持するのに必要な最小ページ数 (meta=0 + header=1 + record pages)。
    /// <c>_hwm=0</c> でも meta/header の 2 ページは残す。
    /// </summary>
    internal long ComputeRequiredPageCount()
        => _hwm == 0 ? 2L : ((_hwm - 1) / RecordsPerPage) + 3L;

    /// <summary>内部 PagedFile への参照 (vacuum/truncate 経路で使用)。</summary>
    internal IPagedFile UnderlyingFile => _file;

    /// <summary>テスト用。free list 先頭 (-1 で空)。</summary>
    internal long FreeHead => _freeHead;

    /// <summary>
    /// vacuum: 可視性フィルタを通さない raw 読み取り。<paramref name="id"/> 範囲外は
    /// InUse=false の値を返す。
    /// </summary>
    internal RawVertexRecord ReadRaw(long id)
    {
        if (id < 0 || id >= _hwm) return default;
        var (pageId, off) = Location(id);
        var meta = _versions.Read(id); // xmin/xmax は sidecar から
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        return new RawVertexRecord
        {
            InUse = (rec[0] & FlagInUse) != 0,
            FirstEdgeId = new EdgeId(RecordHelpers.ReadInt48(rec[1..])),
            FirstPropId = new PropertyId(RecordHelpers.ReadInt48(rec[7..])),
            Label = new LabelId(BinaryPrimitives.ReadInt16LittleEndian(rec[13..])),
            Xmin = meta.Xmin,
            Xmax = meta.Xmax,
        };
    }

    /// <summary>vacuum: Vertexの FirstPropId を書き換える。chain 整理用。</summary>
    internal void UpdateFirstPropId(VertexId vertexId, PropertyId newFirstPropId)
    {
        var (pageId, off) = Location(vertexId.Sequence);
        var ph = _file.PinForWrite(pageId);
        RecordHelpers.WriteInt48(ph.Data[(off + 7)..], newFirstPropId.Sequence); // Int48 は Sequence
        _file.UnpinDirty(pageId, 0);
    }

    /// <summary>vacuum: Vertexの FirstEdgeId raw 取得。<see cref="GetFirstEdgeId"/> の internal エイリアス。</summary>
    internal EdgeId GetFirstEdgeIdRaw(VertexId vertexId) => GetFirstEdgeId(vertexId);

    /// <summary>vacuum: Vertexの FirstEdgeId 書き換え。<see cref="UpdateFirstEdgeId"/> の internal エイリアス。</summary>
    internal void UpdateFirstEdgeIdRaw(VertexId vertexId, EdgeId newFirstEdgeId)
        => UpdateFirstEdgeId(vertexId, newFirstEdgeId);

    // --- private ---

    private (PageId pageId, int offset) Location(long id)
    {
        int rpp = RecordsPerPage;
        return (new PageId(id / rpp + 2), (int)(id % rpp) * RecordSize);
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.VertexRecord);
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
            throw new StorageFormatMismatchException("vertices", v, StorageFormatVersion.Current);
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
