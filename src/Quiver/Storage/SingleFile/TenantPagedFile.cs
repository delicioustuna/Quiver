using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage.Wal;

namespace Quiver.Storage;

/// <summary>
/// <see cref="SingleFileContainer"/> 内の 1 テナント (= 1 ストアの論理ページ空間) を
/// <see cref="IPagedFile"/> として見せる薄い変換シム。各ストア (NodeStore など) は自分が
/// <c>page 0,1,2..</c> を所有していると思い込んだまま無改修で動く。
///
/// 論理ページ ID は per-tenant page table で物理ページ ID に写像し、pin / alloc / free / unpin は
/// 共有 <see cref="IPagedFile"/> に物理 ID で委譲する。論理 page 0 は予約 (ストアは触らない)。
///
/// page table エントリの符号化 (in-memory &amp; 永続とも同じ):
///   <list type="bullet">
///     <item><c>&gt;= 0</c>: 割当済み物理ページ ID</item>
///     <item><c>== -1</c>: 未割当 (unmapped)</item>
///     <item><c>&lt;= -2</c>: 論理 free list のリンク。次 free 論理 ID = <c>-entry - 2</c>
///       (0 = 末尾番兵。論理 0 は予約のため free にならない)</item>
///   </list>
/// </summary>
internal sealed class TenantPagedFile : IPagedFile
{
    private readonly SingleFileContainer _container;
    private readonly IPagedFile _physical;
    private readonly SingleFileContainer.CatalogEntry _entry;
    private readonly PageKind _defaultKind;
    private readonly object _gate = new();

    // index = 論理ページ ID, value = 上記符号化。
    private readonly List<long> _pageTable = new();
    // page-table 連鎖の物理ページ ID 列 (chain 順)。
    private readonly List<long> _ptPages = new();

    private static int EntriesPerPage => SingleFileContainer.EntriesPerPageTablePage;

    internal TenantPagedFile(
        SingleFileContainer container, IPagedFile physical,
        SingleFileContainer.CatalogEntry entry, PageKind defaultKind)
    {
        _container = container;
        _physical = physical;
        _entry = entry;
        _defaultKind = defaultKind;
        LoadPageTable();
    }

    int IPagedFile.PageSize => PagedFile.PageSizeConst;
    public long PageCount => _entry.LogicalPageCount;
    public string Path => _container.Path;

    public PageId AllocatePage(PageKind kind)
    {
        lock (_gate)
        {
            long logical;
            if (_entry.LogicalFreeHead != 0)
            {
                // 論理 free list から再利用。
                logical = _entry.LogicalFreeHead;
                long encoded = _pageTable[(int)logical];
                _entry.LogicalFreeHead = -encoded - 2; // 次 free 論理 ID
            }
            else
            {
                logical = _entry.LogicalPageCount;
                _entry.LogicalPageCount++;
                EnsureInMemorySize((int)logical + 1);
            }

            long phys = _container.AllocatePhysical(kind).Value;
            SetPageTableEntry((int)logical, phys);
            _container.PersistCatalogEntry(_entry);
            return new PageId(logical);
        }
    }

    public void FreePage(PageId pageId)
    {
        lock (_gate)
        {
            int logical = (int)pageId.Value;
            if (logical <= 0 || logical >= _pageTable.Count) return;
            long phys = _pageTable[logical];
            if (phys < 0) return; // 既に free / 未割当
            _container.FreePhysical(new PageId(phys));
            // free list に push: エントリに「現 freeHead」をリンクとして符号化。
            SetPageTableEntry(logical, -(_entry.LogicalFreeHead) - 2);
            _entry.LogicalFreeHead = logical;
            _container.PersistCatalogEntry(_entry);
        }
    }

    public PageReadHandle PinForRead(PageId pageId) => _physical.PinForRead(Translate(pageId));
    public PageWriteHandle PinForWrite(PageId pageId) => _physical.PinForWrite(Translate(pageId));
    // journaling モードを物理層へ転送する (mode は物理 pageId でキーされる)。
    public PageWriteHandle PinForWrite(PageId pageId, WalJournalMode mode) => _physical.PinForWrite(Translate(pageId), mode);
    // Unpin / UnpinDirty は PagedFile では明示的インターフェイス実装なのでインターフェイス経由で呼ぶ。
    public void Unpin(PageId pageId) => ((IPagedFile)_physical).Unpin(Translate(pageId));
    public void UnpinDirty(PageId pageId, long lsn) => ((IPagedFile)_physical).UnpinDirty(Translate(pageId), lsn);

    public void Flush() => _physical.Flush();

    // WAL は container 物理層で 1 fileKind に一本化する。
    // テナント単位の per-file WAL は行わないため、ここでは何もしない。
    public void EnableWalLogging(byte fileKind, IWriteAheadLog wal) { }
    public void EnableWalFlushOnly(IWriteAheadLog wal) { }

    public void WritePageForRecovery(PageId pageId, ReadOnlySpan<byte> pageBytes)
        => _physical.WritePageForRecovery(Translate(pageId), pageBytes);

    /// <summary>
    /// vacuum: テナント論理空間を <paramref name="newPageCount"/> 論理ページへ縮小し、除去
    /// される論理ページの物理ページをグローバル free list へ返却する (= 物理ページ再利用での回収)。
    /// 物理ファイル自体は縮まないが、解放ページは他テナントへ再割当できる。
    ///
    /// crash 安全な順序: ① page table エントリを unmapped に + LogicalPageCount 縮小 + カタログ
    /// 永続化 → ② flush (= クリア済みマッピングを durable に) → ③ 物理ページ解放 → ④ flush。
    /// ②の後にクラッシュしても「page table はもう指していない」ので、解放途中でも ABA は起きない。
    /// </summary>
    public void Truncate(long newPageCount)
    {
        lock (_gate)
        {
            if (newPageCount < 1) newPageCount = 1;
            if (newPageCount >= _entry.LogicalPageCount) return; // 拡張は無視

            // 除去される論理ページの物理ページを収集。
            var toFree = new List<long>();
            for (long l = newPageCount; l < _entry.LogicalPageCount; l++)
            {
                if (l < _pageTable.Count && _pageTable[(int)l] >= 0)
                    toFree.Add(_pageTable[(int)l]);
            }

            // ① page table を unmapped に + 論理 free list を破棄 + 論理ページ数を縮小。
            for (long l = newPageCount; l < _entry.LogicalPageCount; l++)
                if (l < _pageTable.Count) SetPageTableEntry((int)l, -1L);
            _entry.LogicalPageCount = newPageCount;
            _entry.LogicalFreeHead = 0; // 除去範囲を指しうる free 連鎖を破棄
            _container.PersistCatalogEntry(_entry);

            // ② クリア済みマッピングを durable 化してから物理ページを解放する。
            _physical.Flush();

            // ③ 物理ページをグローバル free list へ返却 (他テナントが再利用可能)。
            foreach (var p in toFree)
                _container.FreePhysical(new PageId(p));

            // ④ free list を durable 化。
            _physical.Flush();
        }
    }

    // テナントは物理ファイルを所有しない (所有権は SingleFileContainer)。dispose は no-op。
    public void Dispose() { }

    // ------------------------------------------------------------------
    // 論理 → 物理 変換 / page table 管理
    // ------------------------------------------------------------------

    private PageId Translate(PageId logical)
    {
        long l = logical.Value;
        if (l < 0 || l >= _pageTable.Count)
            throw new CorruptionException(
                $"Tenant {_entry.TenantId}: logical page {l} out of range (count={_pageTable.Count}).");
        long phys = _pageTable[(int)l];
        if (phys < 0)
            throw new CorruptionException(
                $"Tenant {_entry.TenantId}: logical page {l} is unmapped.");
        return new PageId(phys);
    }

    private void EnsureInMemorySize(int count)
    {
        while (_pageTable.Count < count) _pageTable.Add(-1L);
    }

    private void SetPageTableEntry(int logical, long value)
    {
        EnsureInMemorySize(logical + 1);
        _pageTable[logical] = value;

        int pageIndex = logical / EntriesPerPage;
        int slot = logical % EntriesPerPage;
        EnsurePageTablePages(pageIndex);

        var wh = _physical.PinForWrite(new PageId(_ptPages[pageIndex]));
        try
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                wh.Data[(SingleFileContainer.PtEntriesOffset + slot * 8)..], value);
        }
        finally { wh.Dispose(); }
    }

    private void EnsurePageTablePages(int pageIndex)
    {
        while (_ptPages.Count <= pageIndex)
        {
            var newPt = _container.AllocatePhysical(PageKind.PageTable);
            // 新 page-table ページを初期化: next=-1, 全エントリ=-1 (未割当)。
            var wh = _physical.PinForWrite(newPt);
            try
            {
                var body = wh.Data;
                BinaryPrimitives.WriteInt64LittleEndian(body[SingleFileContainer.PtNextOffset..], -1L);
                for (int s = 0; s < EntriesPerPage; s++)
                    BinaryPrimitives.WriteInt64LittleEndian(
                        body[(SingleFileContainer.PtEntriesOffset + s * 8)..], -1L);
            }
            finally { wh.Dispose(); }

            if (_ptPages.Count == 0)
            {
                _entry.PageTableHead = newPt.Value;
                _container.PersistCatalogEntry(_entry);
            }
            else
            {
                long prev = _ptPages[^1];
                var wh2 = _physical.PinForWrite(new PageId(prev));
                try
                {
                    BinaryPrimitives.WriteInt64LittleEndian(wh2.Data[SingleFileContainer.PtNextOffset..], newPt.Value);
                }
                finally { wh2.Dispose(); }
            }
            _ptPages.Add(newPt.Value);
        }
    }

    /// <summary>
    /// recovery / abort 後に in-memory page table をディスクから再読込する。<see cref="_entry"/> は
    /// <see cref="SingleFileContainer.ReloadAll"/> が in-place 更新済みである前提。
    /// </summary>
    internal void ReloadPageTable()
    {
        lock (_gate) LoadPageTable();
    }

    private void LoadPageTable()
    {
        _pageTable.Clear();
        _ptPages.Clear();
        int logicalCount = (int)_entry.LogicalPageCount;
        EnsureInMemorySize(Math.Max(1, logicalCount));

        long cur = _entry.PageTableHead;
        int globalSlot = 0;
        while (cur >= 0)
        {
            _ptPages.Add(cur);
            var rh = _physical.PinForRead(new PageId(cur));
            try
            {
                var body = rh.Data;
                long next = BinaryPrimitives.ReadInt64LittleEndian(body[SingleFileContainer.PtNextOffset..]);
                for (int s = 0; s < EntriesPerPage; s++, globalSlot++)
                {
                    if (globalSlot >= logicalCount) continue; // 残りページは chain 整合のため _ptPages にのみ収集
                    long v = BinaryPrimitives.ReadInt64LittleEndian(
                        body[(SingleFileContainer.PtEntriesOffset + s * 8)..]);
                    _pageTable[globalSlot] = v;
                }
                cur = next;
            }
            finally { rh.Dispose(); }
        }
    }
}
