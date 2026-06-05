using System.Buffers;
using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Storage;

/// <summary>
/// ARCH-5c Phase 1 骨格: MVCC 版チェーン付きの可変長レコードを slotted ページ
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
/// レコードへ再内包した統一 MVCC レコードモデル (docs/design/11 §6.3) を実現する。</para>
///
/// <para>backend へは未配線 (Phase 2 で NodeStore を本ヒープへ載せ替える)。可視性は
/// <see cref="VersionVisible"/> デリゲートで注入し、本骨格は MvccContext に依存しない
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
    private const int MetaFormatVersion = 31;  // byte

    private readonly IPagedFile _file;
    private readonly ItemPointerMap _map;
    private long _appendPage; // 末尾 append 先データページ (< 2 で未割当)

    public VersionedRecordHeap(IPagedFile file, ItemPointerMap map)
    {
        _file = file;
        _map = map;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _appendPage = 0;
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

    /// <summary>head version の物理位置を返す (in-place write handle 構築用)。未登録は <see cref="ItemPointer.Null"/>。</summary>
    public ItemPointer GetHead(long seq) => _map.Get(seq);

    /// <summary>FT-15 / recovery 用: ヘッダから append page を読み直す。</summary>
    public void ReloadMeta() => LoadHeader();

    // --- private ---

    private ItemPointer AppendRecord(long xmin, long xmax, ItemPointer next, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
            throw new ArgumentException(
                $"payload {payload.Length}B exceeds max {MaxPayloadSize}B (overflow ページは Phase 3)", nameof(payload));

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

        // 新規データページを割り当てて追記。
        var pid = _file.AllocatePage(PageKind.SlottedHeap);
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
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.Current;
    }
}

/// <summary>
/// ARCH-5c: 版の可視性判定デリゲート。<see cref="VersionedRecordHeap"/> を MvccContext から
/// 切り離し、Phase 2 配線時に <see cref="Quiver.Core.Visibility"/> を注入できるようにする。
/// </summary>
internal delegate bool VersionVisible(long xmin, long xmax);
