using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Storage;

/// <summary>
/// ARCH-5c: 論理 ID (Sequence) → head version の物理位置 (<see cref="ItemPointer"/>) を引く
/// dense 間接層。adjacency / 索引が保持する Sequence キーはこの map 経由で物理位置へ解決する
/// ため、レコードが版更新で別 slot へ移動しても外部キーのバイトは不変に保てる。
///
/// <para>レイアウト (page = 8192B、body = 8160B、entry = 8B):</para>
/// <list type="bullet">
///   <item>Page 0 = PagedFile メタ</item>
///   <item>Page 1 = map ヘッダ (offset 0: hwm int64 / offset 31: format sentinel)</item>
///   <item>Page 2+ = 8B × 1020 entries / page。<c>seq → (page = seq/1020 + 2, slot = seq%1020)</c></item>
/// </list>
///
/// <para>未設定エントリは 0 (= <see cref="ItemPointer.Null"/>)。zero-fill された新規ページが
/// そのまま null を表すため初期化は不要。ARCH-5c Phase 1 では backend 未配線の骨格。</para>
/// </summary>
internal sealed class ItemPointerMap
{
    private const int EntrySize = 8;
    private static readonly PageId HeaderPageId = new(1);
    private const int MetaHwm = 0;             // int64
    private const int MetaFormatVersion = 31;  // byte (他 store と同 offset)

    private static int EntriesPerPage => RecordPageMapping.PageBodySize / EntrySize; // 1020

    private readonly IPagedFile _file;
    private long _hwm;

    public ItemPointerMap(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _hwm = 0;
            SaveMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
        }
    }

    /// <summary>採番済みエントリ数 (= 最大 Sequence + 1)。</summary>
    public long Hwm => _hwm;

    /// <summary>seq の head version ポインタを引く。未設定 / 範囲外は <see cref="ItemPointer.Null"/>。</summary>
    public ItemPointer Get(long seq)
    {
        if (seq < 0 || seq >= _hwm) return ItemPointer.Null;
        var (pid, off) = Location(seq);
        using var h = _file.PinForRead(pid);
        return ItemPointer.Unpack(BinaryPrimitives.ReadInt64LittleEndian(h.Data[off..]));
    }

    /// <summary>seq の head version ポインタを設定する。必要に応じてエントリページを伸長する。</summary>
    public void Set(long seq, ItemPointer ptr)
    {
        EnsureCapacity(seq);
        var (pid, off) = Location(seq);
        using (var ph = _file.PinForWrite(pid))
            BinaryPrimitives.WriteInt64LittleEndian(ph.Data[off..], ptr.Pack());
        if (seq >= _hwm)
        {
            _hwm = seq + 1;
            SaveMeta();
        }
    }

    /// <summary>FT-15 / recovery 用: ヘッダから hwm を読み直す。</summary>
    public void ReloadMeta() => LoadMeta();

    private (PageId pageId, int offset) Location(long seq)
    {
        int epp = EntriesPerPage;
        return (new PageId(seq / epp + 2), (int)(seq % epp) * EntrySize);
    }

    private void EnsureCapacity(long seq)
    {
        var (pid, _) = Location(seq);
        while (_file.PageCount <= pid.Value)
            _file.AllocatePage(PageKind.ItemPointerMap); // zero-fill = 全 entry null
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != FormatVersion.Current)
            throw new FormatVersionMismatchException("itempointermap", v, FormatVersion.Current);
    }

    private void SaveMeta(bool initialise = false)
    {
        using var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.Current;
    }
}
