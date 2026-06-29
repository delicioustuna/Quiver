using System.Buffers;
using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Storage;

/// <summary>
/// MVCC 版チェーン付きの可変長レコードを slotted ページ
/// (<see cref="SlottedPage"/>) に格納するヒープ。論理 ID (Sequence) は
/// <see cref="ItemPointerMap"/> 経由で head version の物理位置へ解決する。
///
/// <para>version レコードレイアウト:</para>
/// <code>
///   [0]  xmin           : i64   — 作成 tx
///   [8]  xmax           : i64   — 削除 tx (0 = 生存)
///   [16] nextVersionPtr : i64   — 直前 (より古い) version の ItemPointer.Pack (0 = 末尾)
///   [24] payload        : 可変  — 呼び出し側が解釈する (label/type + inline props 等)
/// </code>
///
/// <para>更新は新 version を別 slot へ書き、旧 head に xmax をスタンプし、map を新 head へ
/// repoint する (append-at-head の版チェーン)。snapshot reader は head から
/// <c>nextVersionPtr</c> を辿り最初に可視な version を返す。これにより xmin/xmax を
/// レコードへ再内包した統一 MVCC レコードモデルを実現する。</para>
///
/// <para>可視性は <see cref="VersionVisible"/> デリゲートで注入し、MvccContext に依存しない
/// (単体テスト容易性のため)。</para>
/// </summary>
internal sealed class VersionedRecordHeap
{
    /// <summary>version ヘッダ長 (xmin8 + xmax8 + nextPtr8)。</summary>
    public const int VersionHeaderSize = 24;

    private const int OffXmin = 0;
    private const int OffXmax = 8;
    private const int OffNext = 16;

    /// <summary>1 ページに収まる最大レコード長 (slotted ヘッダ + 1 slot 分を除く)。</summary>
    public static int MaxRecordSize =>
        RecordPageMapping.PageBodySize - SlottedPage.SlotDirStart - SlottedPage.SlotEntrySize;

    /// <summary>inline payload の最大長。</summary>
    public static int MaxPayloadSize => MaxRecordSize - VersionHeaderSize;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaAppendPage = 0;      // int64
    private const int MetaFreePageHead = 8;    // int64 (空きページ free list 先頭、0 = 空)
    private const int MetaFormatVersion = 31;  // byte

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private long _appendPage;    // 末尾 append 先データページ (< 2 で未割当)
    private long _freePageHead;  // 空きページ free list 先頭 (< 2 で空)。各空きページ body[0..8) に次リンク。

    public VersionedRecordHeap(IPagedFile file, ItemPointerMap map)
    {
        _file = file;
        _map = map;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _appendPage = 0;
            _freePageHead = 0;
            SaveHeader(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadHeader();
        }
    }

    /// <summary>新規エンティティの最初の version を作成し map に登録する。</summary>
    public ItemPointer Insert(long seq, ReadOnlySpan<byte> payload, long xmin)
    {
        var ptr = AppendRecord(xmin, xmax: 0, next: ItemPointer.Null, payload);
        _map.Set(seq, ptr);
        return ptr;
    }

    /// <summary>
    /// 既存エンティティを更新する。旧 head に xmax=<paramref name="xmin"/> をスタンプし、
    /// 新 version を head として prepend する。
    /// </summary>
    public ItemPointer AppendVersion(long seq, ReadOnlySpan<byte> payload, long xmin)
    {
        var oldPtr = _map.Get(seq);
        if (!oldPtr.IsNull) StampXmaxAt(oldPtr, xmin);
        var ptr = AppendRecord(xmin, xmax: 0, next: oldPtr, payload);
        _map.Set(seq, ptr);
        return ptr;
    }

    /// <summary>論理削除: head version に xmax をスタンプする (チェーンは保持)。</summary>
    public void StampXmax(long seq, long xmax)
    {
        var ptr = _map.Get(seq);
        if (!ptr.IsNull) StampXmaxAt(ptr, xmax);
    }

    /// <summary>
    /// head から版チェーンを辿り、最初に <paramref name="visible"/> を満たす version の
    /// payload (コピー) を返す。可視な version が無ければ false。
    /// </summary>
    public bool TryReadVisible(long seq, VersionVisible visible, out byte[] payload)
        => TryReadVisible(seq, visible, out payload, out _, out _);

    /// <summary>
    /// <see cref="TryReadVisible(long, VersionVisible, out byte[])"/> の拡張。選ばれた version の
    /// xmin / xmax も返す (NodeReadHandle 等が MVCC スタンプを必要とするため)。
    /// </summary>
    public bool TryReadVisible(long seq, VersionVisible visible, out byte[] payload, out long xmin, out long xmax)
    {
        payload = Array.Empty<byte>();
        xmin = 0; xmax = 0;
        var ptr = _map.Get(seq);
        long guard = _map.Hwm + 2; // チェーン長は通常 1〜数件。cycle 防御。
        while (!ptr.IsNull)
        {
            if (--guard < 0)
                throw new CorruptionException("version chain too long or cyclic");

            byte[]? found = null;
            long vXmin, vXmax;
            ItemPointer next;
            using (var h = _file.PinForRead(new PageId(ptr.PageId)))
            {
                var sp = new ReadOnlySlottedPage(h.Data);
                if (!sp.TryGet(ptr.Slot, out var rec))
                    return false; // dangling pointer
                vXmin = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmin..]);
                vXmax = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmax..]);
                next = ItemPointer.Unpack(BinaryPrimitives.ReadInt64LittleEndian(rec[OffNext..]));
                if (visible(vXmin, vXmax))
                    found = rec[VersionHeaderSize..].ToArray();
            }
            if (found != null)
            {
                payload = found;
                xmin = vXmin; xmax = vXmax;
                return true;
            }
            ptr = next;
        }
        return false;
    }

    /// <summary>
    /// 最初に可視な version の payload を <paramref name="dest"/> へコピーする
    /// (byte[] を割り当てない alloc-free 経路。inline property の scalar read で使う)。
    /// 戻り値 = payload 長。戻り値 &gt; <c>dest.Length</c> のときは収まらず dest 未変更 (呼出側は
    /// 割当版 <see cref="TryReadVisible(long, VersionVisible, out byte[])"/> へフォールバック)。
    /// 可視版が無ければ 0 (dest 未変更)。
    /// </summary>
    public int TryReadVisibleInto(long seq, VersionVisible visible, Span<byte> dest, out long xmin, out long xmax)
    {
        xmin = 0; xmax = 0;
        var ptr = _map.Get(seq);
        long guard = _map.Hwm + 2;
        while (!ptr.IsNull)
        {
            if (--guard < 0)
                throw new CorruptionException("version chain too long or cyclic");
            ItemPointer next;
            using (var h = _file.PinForRead(new PageId(ptr.PageId)))
            {
                var sp = new ReadOnlySlottedPage(h.Data);
                if (!sp.TryGet(ptr.Slot, out var rec))
                    return 0; // dangling pointer
                long vXmin = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmin..]);
                long vXmax = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmax..]);
                next = ItemPointer.Unpack(BinaryPrimitives.ReadInt64LittleEndian(rec[OffNext..]));
                if (visible(vXmin, vXmax))
                {
                    var body = rec[VersionHeaderSize..];
                    xmin = vXmin; xmax = vXmax;
                    if (body.Length <= dest.Length) body.CopyTo(dest);
                    return body.Length;
                }
            }
            ptr = next;
        }
        return 0;
    }

    /// <summary>
    /// head version の生 payload (コピー) + xmin/xmax を可視性フィルタ無しで返す。vacuum / raw 読み
    /// 取り用。エントリが無ければ false。
    /// </summary>
    public bool TryReadHeadRaw(long seq, out byte[] payload, out long xmin, out long xmax)
    {
        payload = Array.Empty<byte>();
        xmin = 0; xmax = 0;
        var ptr = _map.Get(seq);
        if (ptr.IsNull) return false;
        using var h = _file.PinForRead(new PageId(ptr.PageId));
        var sp = new ReadOnlySlottedPage(h.Data);
        if (!sp.TryGet(ptr.Slot, out var rec)) return false;
        xmin = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmin..]);
        xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmax..]);
        payload = rec[VersionHeaderSize..].ToArray();
        return true;
    }

    /// <summary>
    /// head version を <b>1 回の pin</b> で読む alloc-free 経路。<paramref name="dest"/> へ
    /// payload 先頭をコピー (収まる分だけ) し、head の xmin/xmax と「より古い版が続くか」
    /// (<paramref name="hasOlderVersion"/>) を返す。戻り値 = payload 全長 (0 = エントリ無し)。
    ///
    /// <para>構造フィールドだけ要る呼出側 (<see cref="Records.VersionedRelationshipStore.Read"/>) は固定長
    /// prefix span を渡せばよい (payload 全長 &gt; <c>dest.Length</c> でも先頭はコピー済み)。可視性は
    /// 呼出側が xmin/xmax で判定し、head 不可視かつ <paramref name="hasOlderVersion"/> のときだけ
    /// <see cref="TryReadVisible(long, VersionVisible, out byte[])"/> へフォールバックする
    /// (= 従来の per-read 2 回 pin + 破棄 ToArray を最頻ケースで省く)。</para>
    /// </summary>
    public int TryReadHeadInto(long seq, Span<byte> dest, out long xmin, out long xmax, out bool hasOlderVersion)
    {
        xmin = 0; xmax = 0; hasOlderVersion = false;
        var ptr = _map.Get(seq);
        if (ptr.IsNull) return 0;
        using var h = _file.PinForRead(new PageId(ptr.PageId));
        var sp = new ReadOnlySlottedPage(h.Data);
        if (!sp.TryGet(ptr.Slot, out var rec)) return 0;
        xmin = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmin..]);
        xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmax..]);
        hasOlderVersion = !ItemPointer.Unpack(BinaryPrimitives.ReadInt64LittleEndian(rec[OffNext..])).IsNull;
        var body = rec[VersionHeaderSize..];
        int n = Math.Min(body.Length, dest.Length);
        body[..n].CopyTo(dest);
        return body.Length;
    }

    /// <summary>head version の物理位置を返す (in-place write handle 構築用)。未登録は <see cref="ItemPointer.Null"/>。</summary>
    public ItemPointer GetHead(long seq) => _map.Get(seq);

    /// <summary>
    /// head version の payload を新しい内容へ更新する。head が同一 tx の
    /// 未コミット版 (xmin==currentTxId, xmax==0) なら **in-place 置換** (版を増やさず intra-tx
    /// bloat を避ける)、それ以外 (可視な committed 版) なら **copy-on-write** で新版を prepend する。
    /// inline property の set/remove で使う。未登録 seq は新規 Insert。
    /// </summary>
    public ItemPointer AppendOrReplaceHead(long seq, ReadOnlySpan<byte> payload, long currentTxId)
    {
        var ptr = _map.Get(seq);
        if (ptr.IsNull) return Insert(seq, payload, currentTxId);

        long xmin, xmax;
        ItemPointer next;
        using (var h = _file.PinForRead(new PageId(ptr.PageId)))
        {
            var sp = new ReadOnlySlottedPage(h.Data);
            if (!sp.TryGet(ptr.Slot, out var rec))
                return AppendVersion(seq, payload, currentTxId);
            xmin = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmin..]);
            xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmax..]);
            next = ItemPointer.Unpack(BinaryPrimitives.ReadInt64LittleEndian(rec[OffNext..]));
        }

        // 他 tx から見えない自分の未コミット head → in-place 置換を試みる (版ヘッダ保持)。
        if (xmin == currentTxId && xmax == 0)
        {
            if (payload.Length > MaxPayloadSize)
                throw new ArgumentException($"payload {payload.Length}B exceeds max {MaxPayloadSize}B", nameof(payload));
            int total = VersionHeaderSize + payload.Length;
            byte[] buf = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                var rec = buf.AsSpan(0, total);
                BinaryPrimitives.WriteInt64LittleEndian(rec[OffXmin..], xmin);
                BinaryPrimitives.WriteInt64LittleEndian(rec[OffXmax..], xmax);
                BinaryPrimitives.WriteInt64LittleEndian(rec[OffNext..], next.Pack());
                payload.CopyTo(rec[VersionHeaderSize..]);
                using var ph = _file.PinForWrite(new PageId(ptr.PageId));
                var sp = new SlottedPage(ph.Data);
                if (sp.TryUpdate(ptr.Slot, rec))
                    return ptr; // slot index 保持 → map 不変
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            // ページに収まらない → copy-on-write へフォールバック (旧自版は xmax=self で self-invisible)。
        }

        return AppendVersion(seq, payload, currentTxId);
    }

    /// <summary>
    /// head を残し、版チェーン上の dead な非 head 版 (property 更新の
    /// copy-on-write で生じた旧版) を回収する。<paramref name="reclaimable"/> が true を返す版を
    /// tombstone し、生存版を再リンクする。head (最新版) は常に保持。回収数を返す。
    /// </summary>
    public int PruneDeadVersions(long seq, Func<long, long, bool> reclaimable)
    {
        var head = _map.Get(seq);
        if (head.IsNull) return 0;

        // チェーンを収集 (head が index 0)。
        var chain = new List<(ItemPointer Ptr, long Xmin, long Xmax)>();
        var ptr = head;
        long guard = _map.Hwm + 2;
        while (!ptr.IsNull)
        {
            if (--guard < 0) throw new CorruptionException("version chain too long or cyclic");
            ItemPointer next;
            using (var h = _file.PinForRead(new PageId(ptr.PageId)))
            {
                var sp = new ReadOnlySlottedPage(h.Data);
                if (!sp.TryGet(ptr.Slot, out var rec)) break;
                long xmin = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmin..]);
                long xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmax..]);
                next = ItemPointer.Unpack(BinaryPrimitives.ReadInt64LittleEndian(rec[OffNext..]));
                chain.Add((ptr, xmin, xmax));
            }
            ptr = next;
        }
        if (chain.Count <= 1) return 0;

        // head は常に保持。非 head のうち reclaimable を tombstone、生存版を kept に。
        var kept = new List<ItemPointer> { chain[0].Ptr };
        HashSet<long>? touched = null;
        int removed = 0;
        for (int i = 1; i < chain.Count; i++)
        {
            if (reclaimable(chain[i].Xmin, chain[i].Xmax))
            {
                using (var ph = _file.PinForWrite(new PageId(chain[i].Ptr.PageId)))
                    new SlottedPage(ph.Data).Delete(chain[i].Ptr.Slot);
                (touched ??= new HashSet<long>()).Add(chain[i].Ptr.PageId);
                removed++;
            }
            else
            {
                kept.Add(chain[i].Ptr);
            }
        }
        if (removed == 0) return 0;

        // 生存版を順に再リンク (kept[j].nextPtr = kept[j+1] or null)。
        for (int j = 0; j < kept.Count; j++)
        {
            var next = j + 1 < kept.Count ? kept[j + 1] : ItemPointer.Null;
            using var ph = _file.PinForWrite(new PageId(kept[j].PageId));
            var sp = new SlottedPage(ph.Data);
            if (sp.TryGetMutable(kept[j].Slot, out var rec))
                BinaryPrimitives.WriteInt64LittleEndian(rec[OffNext..], next.Pack());
        }
        // tombstone で空になったページを free list へ回収する。
        if (touched != null)
            foreach (var pg in touched) FreePageIfEmpty(pg);
        return removed;
    }

    /// <summary>
    /// seq の全 version を物理回収する (vacuum 用)。head から nextVersionPtr を辿って各 slot を
    /// tombstone し、map エントリを null にする。バイトの実回収は次回 insert 時の compaction で行う。
    /// </summary>
    public void Remove(long seq)
    {
        var ptr = _map.Get(seq);
        long guard = _map.Hwm + 2;
        HashSet<long>? touched = null;
        while (!ptr.IsNull)
        {
            if (--guard < 0)
                throw new CorruptionException("version chain too long or cyclic");
            ItemPointer next;
            using (var ph = _file.PinForWrite(new PageId(ptr.PageId)))
            {
                var sp = new SlottedPage(ph.Data);
                if (!sp.TryGetMutable(ptr.Slot, out var rec)) break;
                next = ItemPointer.Unpack(BinaryPrimitives.ReadInt64LittleEndian(rec[OffNext..]));
                sp.Delete(ptr.Slot);
            }
            (touched ??= new HashSet<long>()).Add(ptr.PageId);
            ptr = next;
        }
        _map.Set(seq, ItemPointer.Null);
        // 空になったページを free list へ回収する (チェーン walk 後にまとめて、
        // 同一ページの多重 free を避ける)。
        if (touched != null)
            foreach (var pg in touched) FreePageIfEmpty(pg);
    }

    /// <summary>recovery 用: ヘッダから append page を読み直す。</summary>
    public void ReloadMeta() => LoadHeader();

    // --- private ---

    private ItemPointer AppendRecord(long xmin, long xmax, ItemPointer next, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new ArgumentException(
                $"payload {payload.Length}B exceeds max {MaxPayloadSize}B", nameof(payload));

        int total = VersionHeaderSize + payload.Length;
        byte[] buf = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var rec = buf.AsSpan(0, total);
            BinaryPrimitives.WriteInt64LittleEndian(rec[OffXmin..], xmin);
            BinaryPrimitives.WriteInt64LittleEndian(rec[OffXmax..], xmax);
            BinaryPrimitives.WriteInt64LittleEndian(rec[OffNext..], next.Pack());
            payload.CopyTo(rec[VersionHeaderSize..]);
            return InsertRecord(rec);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private ItemPointer InsertRecord(ReadOnlySpan<byte> rec)
    {
        // 末尾ページに追記を試みる (compaction はページ内で SlottedPage が処理)。
        if (_appendPage >= 2)
        {
            using var ph = _file.PinForWrite(new PageId(_appendPage));
            var sp = new SlottedPage(ph.Data);
            if (sp.TryInsert(rec, out int slot))
                return new ItemPointer(_appendPage, slot);
        }

        // 末尾ページが満杯なら、まず vacuum 回収済みの空きページを再利用する
        // (free list が空のときだけ物理ページを新規割り当て)。これで churn 下の物理成長を抑える。
        PageId pid = PopFreePageOrAllocate();
        int newSlot;
        using (var ph = _file.PinForWrite(pid))
        {
            var sp = new SlottedPage(ph.Data);
            sp.Init();
            if (!sp.TryInsert(rec, out newSlot))
                throw new InvalidOperationException("record does not fit in an empty page");
        }
        _appendPage = pid.Value;
        SaveHeader();
        return new ItemPointer(pid.Value, newSlot);
    }

    /// <summary>空きページ free list から 1 ページ pop する。空なら物理ページを新規割り当て。</summary>
    private PageId PopFreePageOrAllocate()
    {
        if (_freePageHead >= 2)
        {
            var pid = new PageId(_freePageHead);
            long next;
            using (var h = _file.PinForRead(pid))
                next = BinaryPrimitives.ReadInt64LittleEndian(h.Data); // body[0..8) = 次リンク
            _freePageHead = next;
            return pid;
        }
        return _file.AllocatePage(PageKind.SlottedHeap);
    }

    /// <summary>
    /// live スロットが 0 になった heap データページを free list へ回収する。
    /// 現在の append 先ページは除外する (継続して追記するため)。vacuum (Remove/PruneDeadVersions)
    /// から、スロット削除後に touch したページに対して呼ぶ。
    /// </summary>
    private void FreePageIfEmpty(long pageId)
    {
        if (pageId < 2 || pageId == _appendPage) return;
        // 読取で空判定 (空でなければ dirty にしない)。
        bool empty;
        using (var h = _file.PinForRead(new PageId(pageId)))
            empty = new ReadOnlySlottedPage(h.Data).HasNoLiveSlots();
        if (!empty) return;
        // body[0..8) に旧 free head を書き、この空きページを新 head にする。
        // (live データは無いので slotted ヘッダを潰しても安全。再利用時に Init で上書きする)
        using (var ph = _file.PinForWrite(new PageId(pageId)))
            BinaryPrimitives.WriteInt64LittleEndian(ph.Data, _freePageHead);
        _freePageHead = pageId;
        SaveHeader();
    }

    private void StampXmaxAt(ItemPointer ptr, long xmax)
    {
        using var ph = _file.PinForWrite(new PageId(ptr.PageId));
        var sp = new SlottedPage(ph.Data);
        if (sp.TryGetMutable(ptr.Slot, out var rec))
            BinaryPrimitives.WriteInt64LittleEndian(rec[OffXmax..], xmax);
    }

    private void LoadHeader()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _appendPage = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaAppendPage..]);
        _freePageHead = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaFreePageHead..]);
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != FormatVersion.Current)
            throw new FormatVersionMismatchException("versionedheap", v, FormatVersion.Current);
    }

    private void SaveHeader(bool initialise = false)
    {
        using var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaAppendPage..], _appendPage);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreePageHead..], _freePageHead);
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.Current;
    }
}

/// <summary>
/// 版の可視性判定デリゲート。<see cref="VersionedRecordHeap"/> を MvccContext から
/// 切り離し、<see cref="Quiver.Core.Visibility"/> を注入できるようにする。
/// </summary>
internal delegate bool VersionVisible(long xmin, long xmax);
