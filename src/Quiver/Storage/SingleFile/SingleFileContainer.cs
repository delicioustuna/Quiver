using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage.Wal;

namespace Quiver.Storage;

/// <summary>
/// 単一物理ファイル <c>*.quiver</c> 上に複数の「テナント」(= 各ストアの論理ページ空間) を
/// 同居させるコンテナ。物理層は既存 <see cref="PagedFile"/> をそのまま 1 個だけ再利用し
/// (Clock buffer pool / MMF / WAL logging / ARIES recovery / truncate)、その上に
/// <b>カタログ (テナント記述子)</b> と各テナントの <b>論理→物理 page table</b> を載せる。
///
/// レイアウト:
/// <code>
///   物理 page 0  … PagedFile メタ (FirstFree / PageCount) — PagedFile が管理
///   物理 page 1  … カタログ root (テナント記述子テーブル + 連鎖ポインタ)
///   物理 page 2+ … 各テナントの page-table ページ群 / データページ (物理空間で混在)
/// </code>
///
/// option B: 全ページが単一の物理ページ空間に属するため、WAL / recovery は純物理ページ単位で
/// 動き続ける (論理→物理変換は runtime のみで recovery に漏れない)。
/// </summary>
internal sealed class SingleFileContainer : IDisposable
{
    // カタログ root は物理 page 1 固定。
    internal static readonly PageId CatalogRootPageId = new(1);

    // カタログ root body レイアウト
    private const int CatalogNextOffset = 0;          // int64: 次カタログページ物理 ID (-1 = なし)
    private const int CatalogCountOffset = 8;          // int64: 記述子件数
    // クリーン終了で WAL を削除しても MVCC visibility / TxId 採番を継続できるよう、
    // 「これ未満の TxId は committed と presume してよい」高水位 (= 終了時の次 TxId) を root に保持する。
    private const int CommittedHighWaterOffset = 16;   // int64: committed TxId 高水位 (= 次採番 TxId)
    private const int CatalogDescriptorsOffset = 24;   // 以降 DescriptorSize バイトずつ

    // 記述子: tenantId(1) flags(1) pageTableHead(8) logicalPageCount(8) logicalFreeHead(8) = 26B
    private const int DescriptorSize = 26;

    // page-table ページ body レイアウト
    internal const int PtNextOffset = 0;        // int64: 次 page-table ページ物理 ID (-1 = なし)
    internal const int PtEntriesOffset = 8;     // 以降 int64 物理 ID エントリの配列
    internal static int EntriesPerPageTablePage =>
        (PagedFile.PageSizeConst - PageHeader.Size - PtEntriesOffset) / 8; // 1019

    private readonly IPagedFile _physical;
    private readonly Dictionary<byte, CatalogEntry> _catalog = new();
    private readonly Dictionary<byte, TenantPagedFile> _tenants = new();
    private readonly object _gate = new();
    private bool _disposed;
    // committed TxId 高水位 (= 最終クリーン終了時の次採番 TxId)。0 = 未設定。
    private long _committedHighWaterTxId;

    public string Path => _physical.Path;
    internal IPagedFile Physical => _physical;

    /// <summary>
    /// 永続化されている committed TxId 高水位。クリーン終了で WAL を削除しても、
    /// reopen 時に「これ未満の TxId は committed」と presume して MVCC visibility を維持し、
    /// 次 TxId 採番をここから継続するために factory が参照する。0 = 未設定 (WAL から復元)。
    /// </summary>
    public long CommittedHighWaterTxId
    {
        get { lock (_gate) return _committedHighWaterTxId; }
    }

    /// <summary>
    /// クリーン終了時に backend が呼び、終了時点の次採番 TxId をカタログ root へ
    /// 書き込む。実際の durable 化は呼び出し側の <c>FlushAll</c> に委ねる (本メソッドは buffer pool 更新)。
    /// </summary>
    public void SetCommittedHighWaterTxId(long value)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _committedHighWaterTxId = value;
            var wh = _physical.PinForWrite(CatalogRootPageId);
            try
            {
                BinaryPrimitives.WriteInt64LittleEndian(wh.Data[CommittedHighWaterOffset..], value);
            }
            finally { wh.Dispose(); }
        }
    }

    public SingleFileContainer(string path, int poolCapacityPages = 256)
        : this(new PagedFile(path, poolCapacityPages))
    {
    }

    /// <summary>
    /// 指定した物理ページ実装上に単一ファイルコンテナを構築する。
    /// インメモリ物理層など、ファイルを持たない実装の組み立てに使用する。
    /// </summary>
    internal SingleFileContainer(IPagedFile physical)
    {
        _physical = physical;
        if (_physical.PageCount <= 1)
        {
            // 新規ファイル: カタログ root を物理 page 1 に確保して初期化する。
            var root = _physical.AllocatePage(PageKind.Catalog);
            if (root != CatalogRootPageId)
                throw new CorruptionException(
                    $"Catalog root expected at physical page {CatalogRootPageId.Value}, got {root.Value}.");
            var wh = _physical.PinForWrite(root);
            try
            {
                var body = wh.Data;
                BinaryPrimitives.WriteInt64LittleEndian(body[CatalogNextOffset..], -1L);
                BinaryPrimitives.WriteInt64LittleEndian(body[CatalogCountOffset..], 0L);
            }
            finally { wh.Dispose(); }
        }
        else
        {
            LoadCatalog();
        }
    }

    /// <summary>
    /// テナント <paramref name="tenantId"/> の論理ページ空間を <see cref="IPagedFile"/> として開く。
    /// 既存なら同一インスタンスを返し、未存在なら新規記述子を作って永続化する。
    /// </summary>
    /// <summary>
    /// テナント <paramref name="tenantId"/> がカタログに既存か (= 過去に一度
    /// open / 永続化されたか) を、新規作成せずに判定する。adjacency のように「bulk load 済みなら
    /// 開く / 無ければスキップ」を判断する factory 経路で使う。
    /// </summary>
    public bool HasTenant(byte tenantId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _catalog.ContainsKey(tenantId);
        }
    }

    public IPagedFile OpenTenant(byte tenantId, PageKind defaultKind)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tenants.TryGetValue(tenantId, out var existing))
                return existing;

            if (!_catalog.TryGetValue(tenantId, out var entry))
            {
                // 新規テナント: logicalPageCount=1 (論理 page 0 予約), freeHead=0 (空), pageTableHead=-1。
                entry = new CatalogEntry(tenantId)
                {
                    LogicalPageCount = 1,
                    LogicalFreeHead = 0,
                    PageTableHead = -1,
                };
                _catalog[tenantId] = entry;
                PersistCatalogLocked();
            }

            var tenant = new TenantPagedFile(this, _physical, entry, defaultKind);
            _tenants[tenantId] = tenant;
            return tenant;
        }
    }

    // ------------------------------------------------------------------
    // TenantPagedFile から呼ばれる物理ページ操作 / 永続化フック
    // ------------------------------------------------------------------

    internal PageId AllocatePhysical(PageKind kind) => _physical.AllocatePage(kind);

    internal void FreePhysical(PageId phys) => _physical.FreePage(phys);

    /// <summary>
    /// option B: 全テナント (= 物理ページ全体) を単一の <paramref name="dataFileKind"/> で WAL
    /// ロギング対象にする。物理ページ ID は全テナント横断で一意なので、WAL / recovery は純物理
    /// ページ単位で動く。recovery 時は fileRegistry に <c>{ dataFileKind: container.Physical }</c>
    /// を渡せば PageImage / CLR が物理ページへ透過適用される。
    /// </summary>
    internal void EnableWalLogging(byte dataFileKind, IWriteAheadLog wal)
        => _physical.EnableWalLogging(dataFileKind, wal);

    /// <summary>
    /// recovery / abort (CLR undo) が物理 page1 (カタログ root) と page-table ページを書き戻した
    /// 後に、in-memory のテナント記述子 (CatalogEntry) と各 open テナントの page table を
    /// ディスクから再同期する。記述子は <b>in-place 更新</b>するため、open 済みテナントが保持する
    /// CatalogEntry 参照はそのまま有効。
    /// </summary>
    internal void ReloadAll()
    {
        lock (_gate)
        {
            if (_tenants.Count == 0)
            {
                // recovery 直後 (テナント未 open): カタログを丸ごと読み直す。kill 後の reopen で
                // ctor が読んだ stale カタログを、recovery が物理 page1 を復元した後の正本で置換する。
                LoadCatalog(inPlace: false);
            }
            else
            {
                // abort (CLR undo) 後: open 済みテナントの CatalogEntry 参照を保つため in-place 更新。
                LoadCatalog(inPlace: true);
                foreach (var tenant in _tenants.Values)
                    tenant.ReloadPageTable();
            }
        }
    }

    /// <summary>記述子 (logicalPageCount / freeHead / pageTableHead) の変更をカタログページへ書き戻す。</summary>
    internal void PersistCatalogEntry(CatalogEntry entry)
    {
        lock (_gate)
        {
            _catalog[entry.TenantId] = entry;
            PersistCatalogLocked();
        }
    }

    public void Flush() => _physical.Flush();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _physical.Dispose();
    }

    // ------------------------------------------------------------------
    // カタログ I/O
    // ------------------------------------------------------------------

    private void LoadCatalog(bool inPlace = false)
    {
        if (!inPlace) _catalog.Clear();
        long catalogPage = CatalogRootPageId.Value;
        while (catalogPage >= 0)
        {
            var rh = _physical.PinForRead(new PageId(catalogPage));
            try
            {
                var body = rh.Data;
                long next = BinaryPrimitives.ReadInt64LittleEndian(body[CatalogNextOffset..]);
                long count = BinaryPrimitives.ReadInt64LittleEndian(body[CatalogCountOffset..]);
                // committed TxId 高水位は root ページ (page 1) にのみ持つ。
                if (catalogPage == CatalogRootPageId.Value)
                    _committedHighWaterTxId = BinaryPrimitives.ReadInt64LittleEndian(body[CommittedHighWaterOffset..]);
                int offset = CatalogDescriptorsOffset;
                for (long i = 0; i < count && offset + DescriptorSize <= body.Length; i++, offset += DescriptorSize)
                {
                    var entry = ReadDescriptor(body[offset..]);
                    // in-place: open 済みテナントの CatalogEntry 参照を保つため、既存はフィールド更新。
                    if (inPlace && _catalog.TryGetValue(entry.TenantId, out var existing))
                    {
                        existing.PageTableHead = entry.PageTableHead;
                        existing.LogicalPageCount = entry.LogicalPageCount;
                        existing.LogicalFreeHead = entry.LogicalFreeHead;
                    }
                    else
                    {
                        _catalog[entry.TenantId] = entry;
                    }
                }
                catalogPage = next;
            }
            finally { rh.Dispose(); }
        }
    }

    // 現状は単一カタログページ前提 (記述子 ~313 件まで)。連鎖は将来拡張。
    private void PersistCatalogLocked()
    {
        if (_catalog.Count * DescriptorSize + CatalogDescriptorsOffset >
            PagedFile.PageSizeConst - PageHeader.Size)
            throw new StorageException(
                $"Catalog descriptor table overflow ({_catalog.Count} tenants). Chained catalog pages not yet implemented.");

        var wh = _physical.PinForWrite(CatalogRootPageId);
        try
        {
            var body = wh.Data;
            BinaryPrimitives.WriteInt64LittleEndian(body[CatalogNextOffset..], -1L);
            BinaryPrimitives.WriteInt64LittleEndian(body[CatalogCountOffset..], _catalog.Count);
            int offset = CatalogDescriptorsOffset;
            foreach (var entry in _catalog.Values)
            {
                WriteDescriptor(body[offset..], entry);
                offset += DescriptorSize;
            }
        }
        finally { wh.Dispose(); }
    }

    private static CatalogEntry ReadDescriptor(ReadOnlySpan<byte> span)
    {
        byte tenantId = span[0];
        // span[1] = flags (予約)
        long pageTableHead = BinaryPrimitives.ReadInt64LittleEndian(span[2..]);
        long logicalPageCount = BinaryPrimitives.ReadInt64LittleEndian(span[10..]);
        long logicalFreeHead = BinaryPrimitives.ReadInt64LittleEndian(span[18..]);
        return new CatalogEntry(tenantId)
        {
            PageTableHead = pageTableHead,
            LogicalPageCount = logicalPageCount,
            LogicalFreeHead = logicalFreeHead,
        };
    }

    private static void WriteDescriptor(Span<byte> span, CatalogEntry entry)
    {
        span[0] = entry.TenantId;
        span[1] = 0; // flags 予約
        BinaryPrimitives.WriteInt64LittleEndian(span[2..], entry.PageTableHead);
        BinaryPrimitives.WriteInt64LittleEndian(span[10..], entry.LogicalPageCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[18..], entry.LogicalFreeHead);
    }

    /// <summary>テナント記述子 (カタログ 1 件)。可変。TenantPagedFile が in-place 更新する。</summary>
    internal sealed class CatalogEntry(byte tenantId)
    {
        public byte TenantId { get; } = tenantId;
        /// <summary>page-table 連鎖の先頭物理ページ ID (-1 = まだ無し)。</summary>
        public long PageTableHead { get; set; } = -1;
        /// <summary>論理ページ数 (= 次に末尾割当する論理 ID)。1 から開始 (論理 page 0 予約)。</summary>
        public long LogicalPageCount { get; set; } = 1;
        /// <summary>論理 free list の先頭論理 ID (0 = 空。論理 0 は予約で free にならないため番兵)。</summary>
        public long LogicalFreeHead { get; set; }
    }
}
