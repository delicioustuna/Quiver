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
///   <item>Page 1 = map ヘッダ (offset 0: hwm i64 / offset 8: freeHead i64 / offset 31: format sentinel)</item>
///   <item>Page 2+ = 8B × 1020 entries / page。<c>seq → (page = seq/1020 + 2, slot = seq%1020)</c></item>
/// </list>
///
/// <para>各 entry の i64 値の意味:</para>
/// <list type="bullet">
///   <item><c>0</c>  … 未割当 (null)。zero-fill された新規ページがそのまま null を表す。</item>
///   <item><c>&gt; 0</c> … 有効な <see cref="ItemPointer.Pack"/> (page ≥ 2)。</item>
///   <item><c>&lt; 0</c> … free list リンク (vacuum で回収され再利用待ちの seq)。
///         <c>-(nextFreeSeq + 2)</c> で符号化 (terminal = -1)。</item>
/// </list>
/// vacuum が回収した seq は free list へ積まれ、<see cref="PopFreeSeq"/> が再利用する。これにより
/// 旧 NodeStore の slot 再利用 + 世代カウンタ (ARCH-3/5b) と同じ ABA 検出を維持する。
/// </summary>
internal sealed class ItemPointerMap
{
    private const int EntrySize = 8;
    private static readonly PageId HeaderPageId = new(1);
    private const int MetaHwm = 0;             // i64
    private const int MetaFreeHead = 8;        // i64 (-1 = empty)
    private const int MetaFormatVersion = 31;  // byte

    private static int EntriesPerPage => RecordPageMapping.PageBodySize / EntrySize; // 1020

    private readonly IPagedFile _file;
    private long _hwm;
    private long _freeHead;

    public ItemPointerMap(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _hwm = 0;
            _freeHead = -1;
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

    /// <summary>free list 先頭 (-1 で空)。テスト / 診断用。</summary>
    public long FreeHead => _freeHead;

    /// <summary>seq の head version ポインタを引く。未設定 / free / 範囲外は <see cref="ItemPointer.Null"/>。</summary>
    public ItemPointer Get(long seq)
    {
        if (seq < 0 || seq >= _hwm) return ItemPointer.Null;
        long raw = GetRaw(seq);
        return raw > 0 ? ItemPointer.Unpack(raw) : ItemPointer.Null;
    }

    /// <summary>seq の head version ポインタを設定する。必要に応じてエントリページを伸長する。</summary>
    public void Set(long seq, ItemPointer ptr)
    {
        EnsureCapacity(seq);
        SetRaw(seq, ptr.Pack());
        if (seq >= _hwm)
        {
            _hwm = seq + 1;
            SaveMeta();
        }
    }

    /// <summary>
    /// 再利用可能な seq を free list から 1 つ取り出す。空なら -1。新規採番は呼び出し側が
    /// <see cref="Hwm"/> を使う。
    /// </summary>
    public long PopFreeSeq()
    {
        if (_freeHead < 0) return -1;
        long seq = _freeHead;
        long next = DecodeFreeLink(GetRaw(seq));
        _freeHead = next;
        SaveMeta();
        return seq;
    }

    /// <summary>vacuum 回収した seq を free list へ積む (再利用待ち)。</summary>
    public void PushFreeSeq(long seq)
    {
        EnsureCapacity(seq);
        SetRaw(seq, EncodeFreeLink(_freeHead));
        _freeHead = seq;
        SaveMeta();
    }

    /// <summary>FT-15 / recovery 用: ヘッダから hwm / freeHead を読み直す。</summary>
    public void ReloadMeta() => LoadMeta();

    private static long EncodeFreeLink(long nextFreeSeq) => nextFreeSeq < 0 ? -1L : -(nextFreeSeq + 2);
    private static long DecodeFreeLink(long raw) => raw == -1L ? -1L : -raw - 2;

    private long GetRaw(long seq)
    {
        var (pid, off) = Location(seq);
        using var h = _file.PinForRead(pid);
        return BinaryPrimitives.ReadInt64LittleEndian(h.Data[off..]);
    }

    private void SetRaw(long seq, long value)
    {
        var (pid, off) = Location(seq);
        using var ph = _file.PinForWrite(pid);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[off..], value);
    }

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
        _freeHead = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaFreeHead..]);
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
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.Current;
    }
}
