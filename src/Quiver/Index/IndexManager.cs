using System.Buffers.Binary;
using System.Text;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Wal;

namespace Quiver.Index;

/// <summary>
/// ARCH-4 増分5: 各 B+Tree 索引とその索引カタログ (name → tenantId / PropertyTypeFlags) を
/// <see cref="SingleFileContainer"/> 内のテナントとして格納する索引マネージャ。
///
/// 旧実装は索引ごとに <c>*.idx</c> ファイルを別途 new し、<c>.fileKinds</c> (FileKindCatalog) と
/// <c>.idxmeta</c> をサイドカーとして持っていた。本実装ではそれらを全廃し、索引も含めて
/// すべてのページを単一 <c>graph.quiver</c> に同居させる:
/// <list type="bullet">
///   <item>各索引 = コンテナ内テナント (ID は <see cref="IndexTenantRangeStart"/>..
///     <see cref="IndexTenantRangeEnd"/> から動的割当)。物理ページは container の WAL/recovery で
///     透過的に保護される (DataFileKind 1 個に統一)。</item>
///   <item>索引カタログ = 専用テナント <see cref="CatalogTenantId"/>。name → {tenantId, typeFlags} を
///     直列化して保持し、再起動時の materialize に使う。</item>
/// </list>
/// </summary>
internal sealed class IndexManager : IIndexManager, IDisposable
{
    /// <summary>索引カタログを格納する予約テナント (索引テナントレンジ 0x40 の直前)。</summary>
    internal const byte CatalogTenantId = 0x3F;

    /// <summary>索引 B+Tree テナントの割当下限 (旧 FileKindCatalog 予約レンジを踏襲)。</summary>
    internal const byte IndexTenantRangeStart = 0x40;

    /// <summary>索引 B+Tree テナントの割当上限 (含む)。192 索引まで同時保持可能。</summary>
    internal const byte IndexTenantRangeEnd = 0xFF;

    // カタログ header (テナント論理 page 1) body レイアウト。
    private const int CatalogBlobLenOffset = 0;   // int32: 直列化ブロブ長
    private const int CatalogEntryCountOffset = 4; // int32: 索引件数

    private readonly SingleFileContainer _container;
    private readonly bool _ownsContainer;
    private readonly IPagedFile _catalogTenant;

    private readonly Dictionary<string, object> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyTypeFlags> _indexTypes = new(StringComparer.Ordinal);
    // 索引名 → 割当済みテナント ID。永続カタログの正本。
    private readonly Dictionary<string, byte> _indexTenantIds = new(StringComparer.Ordinal);
    // 索引名 → backing テナント (IPagedFile)。page-count 観測 / truncate に使う (内部用)。
    private readonly Dictionary<string, IPagedFile> _indexFiles = new(StringComparer.Ordinal);
    private readonly HashSet<byte> _usedTenantIds = [];

    // PW-18 follow-up: (label, propertyKey) → indexName のバインディング。
    // SchemaApi.CreateIndex から登録され、MergeNode の自動インデックス選択に使われる。
    private readonly Dictionary<(string Label, string PropertyKey), string> _bindings = new();
    private readonly Dictionary<string, (string Label, string PropertyKey)> _bindingByName
        = new(StringComparer.Ordinal);

    /// <summary>本番経路: factory が共有 container を渡す。container の所有権は移らない。</summary>
    public IndexManager(SingleFileContainer container) : this(container, ownsContainer: false) { }

    private IndexManager(SingleFileContainer container, bool ownsContainer)
    {
        _container = container;
        _ownsContainer = ownsContainer;
        _catalogTenant = container.OpenTenant(CatalogTenantId, PageKind.Header);
        LoadCatalogAndMaterialize();
    }

    /// <summary>
    /// ARCH-4 増分5: テスト / ベンチ用。<paramref name="directory"/> 直下に
    /// <c>graph.quiver</c> コンテナを作成 (または開いて) その上に索引テナントを載せた
    /// 単独所有の <see cref="IndexManager"/> を返す。返した IndexManager の
    /// <see cref="Dispose"/> でコンテナも閉じる。本番経路は
    /// <see cref="IndexManager(SingleFileContainer)"/> を使い container 寿命は backend が握る。
    /// </summary>
    internal static IndexManager OpenStandalone(string directory)
    {
        Directory.CreateDirectory(directory);
        var container = new SingleFileContainer(Path.Combine(directory, "graph.quiver"));
        return new IndexManager(container, ownsContainer: true);
    }

    public IBTreeIndex<int>    CreateInt32Index(string name)  => GetOrCreate(name, new Int32KeyCodec(),  PropertyTypeFlags.Int32,  IndexKeyKind.Int32);
    public IBTreeIndex<long>   CreateInt64Index(string name)  => GetOrCreate(name, new Int64KeyCodec(),  PropertyTypeFlags.Int64,  IndexKeyKind.Int64);
    public IBTreeIndex<double> CreateDoubleIndex(string name) => GetOrCreate(name, new DoubleKeyCodec(), PropertyTypeFlags.Double, IndexKeyKind.Double);
    public IBTreeIndex<string> CreateStringIndex(string name) => GetOrCreate(name, new StringKeyCodec(), PropertyTypeFlags.String, IndexKeyKind.String);
    public IBTreeIndex<byte[]> CreateBytesIndex(string name)  => GetOrCreate(name, new BytesKeyCodec(),  PropertyTypeFlags.Bytes,  IndexKeyKind.Bytes);

    /// <summary>
    /// FT-18: 全索引のバッファプールダーティページを fsync する。
    /// <see cref="Quiver.Transactions.Checkpointer"/> がチェックポイント時に呼び、
    /// 索引内容を checkpointLsn 時点で durable にして WAL truncate を安全にする。
    /// ARCH-4: 全索引は単一 container 上のテナントなので 1 回の flush で足りる。
    /// </summary>
    public void FlushAll()
    {
        if (_indexes.Count > 0) _container.Flush();
    }

    /// <summary>
    /// FT-22: 全 B+Tree 索引を走査し、<paramref name="isLive"/> が <c>false</c> を返した
    /// 値 (NodeId.Value 互換 long) を持つ orphan エントリを <paramref name="output"/> に集める。
    /// 戻り値は (走査索引本数, 走査エントリ総数)。<see cref="RemoveOrphans"/> で実削除する。
    /// </summary>
    public (int IndexCount, long EntryCount) CollectOrphans(
        Func<long, bool> isLive,
        ICollection<(string IndexName, byte[] RawKey, long Value)> output)
    {
        int indexCount = 0;
        long entryCount = 0;
        foreach (var (name, idxObj) in _indexes)
        {
            if (idxObj is not IBTreeIndexFlushable flushable) continue;
            indexCount++;
            foreach (var kv in flushable.EnumerateRawEntries())
            {
                entryCount++;
                if (!isLive(kv.Value))
                    output.Add((name, kv.Key, kv.Value));
            }
        }
        return (indexCount, entryCount);
    }

    /// <summary>
    /// FT-22: 与えた orphan 一覧を索引から削除する。索引名で <see cref="_indexes"/> を引き、
    /// <see cref="IBTreeIndexFlushable.DeleteRawEntry"/> で生キー削除する。
    /// </summary>
    public int RemoveOrphans(IEnumerable<(string IndexName, byte[] RawKey, long Value)> orphans)
    {
        int removed = 0;
        foreach (var (name, key, value) in orphans)
        {
            if (!_indexes.TryGetValue(name, out var idxObj)) continue;
            if (idxObj is not IBTreeIndexFlushable flushable) continue;
            if (flushable.DeleteRawEntry(key, value)) removed++;
        }
        return removed;
    }

    public bool DropIndex(string name)
    {
        if (!_indexes.TryGetValue(name, out var idx)) return false;

        (idx as IDisposable)?.Dispose();
        _indexes.Remove(name);
        _indexTypes.Remove(name);
        // ARCH-4: 索引テナントの論理ページをグローバル free list へ回収する
        // (graph.quiver 自体は縮まないが、解放ページは他テナントへ再割当できる)。
        if (_indexFiles.TryGetValue(name, out var tenant))
        {
            tenant.Truncate(1);
            _indexFiles.Remove(name);
        }
        if (_indexTenantIds.TryGetValue(name, out var tenantId))
        {
            _indexTenantIds.Remove(name);
            _usedTenantIds.Remove(tenantId);
        }
        if (_bindingByName.TryGetValue(name, out var key))
        {
            _bindings.Remove(key);
            _bindingByName.Remove(name);
        }
        PersistCatalog();
        return true;
    }

    public IEnumerable<string> ListIndexes() => _indexes.Keys;

    /// <summary>
    /// OP-4 / ARCH-4: 索引名を <paramref name="oldName"/> から <paramref name="newName"/> へ変更する。
    /// 索引はテナント ID で識別されるため、リネームは <b>カタログ上の name キーの付け替えだけ</b>で済む
    /// (テナント / B+Tree 実体・ページ・WAL 意味はすべて不変)。旧実装と異なり物理 rename も
    /// PagedFile の再 open も不要なので、呼び出し側が保持する <see cref="IBTreeIndex{TKey}"/> 参照は
    /// リネーム後も有効なまま残る。
    /// 旧名が存在しないときは <c>false</c> を返す (冪等)。新名が衝突するときは例外。
    /// </summary>
    public bool RenameIndex(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (oldName == newName) return false;

        if (!_indexes.TryGetValue(oldName, out var idxObj))
        {
            return _indexes.ContainsKey(newName);
        }
        if (_indexes.ContainsKey(newName))
            throw new InvalidOperationException(
                $"Index '{newName}' already exists.");

        _indexes.Remove(oldName);
        _indexes[newName] = idxObj;
        if (_indexTypes.Remove(oldName, out var tf)) _indexTypes[newName] = tf;
        if (_indexFiles.Remove(oldName, out var file)) _indexFiles[newName] = file;
        if (_indexTenantIds.Remove(oldName, out var tid)) _indexTenantIds[newName] = tid;

        // バインディングは index 名で逆引きしているので追従させる。
        if (_bindingByName.TryGetValue(oldName, out var binding))
        {
            _bindingByName.Remove(oldName);
            _bindingByName[newName] = binding;
            _bindings[binding] = newName;
        }
        PersistCatalog();
        return true;
    }

    /// <summary>
    /// OP-1 / ARCH-4: 旧実装では索引ごとの <c>*.idx</c> PagedFile を snapshot へ列挙していたが、
    /// 索引は <c>graph.quiver</c> に同居するようになったため、snapshot の page-by-page コピーは
    /// container 物理ファイル (pageManager 経由) が一括カバーする。よって本プロパティは空を返す。
    /// </summary>
    public IEnumerable<IPagedFile> IndexFiles => Array.Empty<IPagedFile>();

    /// <summary>
    /// ARCH-4 (テスト用): 指定索引の backing テナントの論理ページ数を返す。
    /// free-list 回収・再利用の観測に使う (未知の索引名なら 0)。
    /// </summary>
    internal long GetIndexTenantPageCount(string name)
        => _indexFiles.TryGetValue(name, out var f) ? f.PageCount : 0L;

    public void RegisterIndexBinding(string indexName, string label, string propertyKey)
    {
        if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(propertyKey))
            return; // 空メタデータは無視 (旧 CreateIndex 呼び出しとの互換性)
        var key = (label, propertyKey);
        _bindings[key] = indexName;
        _bindingByName[indexName] = key;
    }

    public bool TryGetIndexName(string label, string propertyKey, out string indexName)
    {
        if (_bindings.TryGetValue((label, propertyKey), out var name))
        {
            indexName = name;
            return true;
        }
        indexName = string.Empty;
        return false;
    }

    public IEnumerable<(string IndexName, string Label, string PropertyKey)> ListIndexBindings()
    {
        foreach (var kv in _bindings)
            yield return (kv.Value, kv.Key.Label, kv.Key.PropertyKey);
    }

    /// <summary>
    /// BA-8: returns the <see cref="PropertyTypeFlags"/> the index was first
    /// registered with, or <see cref="PropertyTypeFlags.None"/> if the index
    /// has not been created yet.
    /// </summary>
    public PropertyTypeFlags GetIndexTypeFlags(string name)
        => _indexTypes.TryGetValue(name, out var f) ? f : PropertyTypeFlags.None;

    private IBTreeIndex<TKey> GetOrCreate<TKey>(
        string name, IKeyCodec<TKey> codec, PropertyTypeFlags typeFlag, IndexKeyKind kind)
    {
        if (_indexes.TryGetValue(name, out var existing))
        {
            // BA-8: an index tenant is single-type. Re-opening with a different key
            // type would corrupt the B+ tree, so fail fast instead of silently
            // mixing numeric and string entries. The persisted catalog reloads the
            // recorded type on restart, so this check also covers cross-restart reuse.
            if (_indexTypes.TryGetValue(name, out var stored) && stored != typeFlag)
                throw new ConstraintException(
                    $"Index '{name}' was created as {stored}; cannot reopen it as {typeFlag}.");
            return (IBTreeIndex<TKey>)existing;
        }

        byte tenantId = AllocateTenantId();
        var tenant = _container.OpenTenant(tenantId, PageKind.Header);
        var index = new BTreeIndex<TKey>(tenant, codec, name, kind);
        _indexes[name] = index;
        _indexTypes[name] = typeFlag;
        _indexFiles[name] = tenant;
        _indexTenantIds[name] = tenantId;
        _usedTenantIds.Add(tenantId);
        PersistCatalog();
        return index;
    }

    private byte AllocateTenantId()
    {
        for (int b = IndexTenantRangeStart; b <= IndexTenantRangeEnd; b++)
        {
            if (!_usedTenantIds.Contains((byte)b)) return (byte)b;
        }
        throw new ConstraintException(
            $"索引テナントの予約レンジ ({IndexTenantRangeStart:X2}..{IndexTenantRangeEnd:X2}) を使い切った。" +
            $"同時保持できる索引数は最大 {IndexTenantRangeEnd - IndexTenantRangeStart + 1} 個。");
    }

    // ------------------------------------------------------------------
    // 索引カタログ I/O (専用テナント上のブロブ)
    // ------------------------------------------------------------------

    private void LoadCatalogAndMaterialize()
    {
        long pageCount = _catalogTenant.PageCount;
        if (pageCount < 2) return; // header 未作成 = 索引ゼロ

        int blobLen, entryCount;
        var hh = _catalogTenant.PinForRead(new PageId(1));
        try
        {
            blobLen = BinaryPrimitives.ReadInt32LittleEndian(hh.Data[CatalogBlobLenOffset..]);
            entryCount = BinaryPrimitives.ReadInt32LittleEndian(hh.Data[CatalogEntryCountOffset..]);
        }
        finally { hh.Dispose(); }
        if (blobLen <= 0 || entryCount <= 0) return;

        byte[] blob = new byte[blobLen];
        int off = 0;
        long dataPage = 2;
        while (off < blobLen)
        {
            var rh = _catalogTenant.PinForRead(new PageId(dataPage));
            try
            {
                int n = Math.Min(PagedFile.BodySize, blobLen - off);
                rh.Data[..n].CopyTo(blob.AsSpan(off));
            }
            finally { rh.Dispose(); }
            off += PagedFile.BodySize;
            dataPage++;
        }

        int pos = 0;
        for (int i = 0; i < entryCount; i++)
        {
            byte tenantId = blob[pos]; pos += 1;
            var typeFlags = (PropertyTypeFlags)BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(pos)); pos += 8;
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos)); pos += 2;
            string indexName = Encoding.UTF8.GetString(blob, pos, nameLen); pos += nameLen;

            _indexTenantIds[indexName] = tenantId;
            _indexTypes[indexName] = typeFlags;
            _usedTenantIds.Add(tenantId);
            MaterializeIndex(indexName, tenantId, typeFlags);
        }
    }

    private void MaterializeIndex(string name, byte tenantId, PropertyTypeFlags typeFlags)
    {
        var tenant = _container.OpenTenant(tenantId, PageKind.Header);
        object index = typeFlags switch
        {
            PropertyTypeFlags.Int32  => new BTreeIndex<int>(tenant, new Int32KeyCodec(), name, IndexKeyKind.Int32),
            PropertyTypeFlags.Int64  => new BTreeIndex<long>(tenant, new Int64KeyCodec(), name, IndexKeyKind.Int64),
            PropertyTypeFlags.Double => new BTreeIndex<double>(tenant, new DoubleKeyCodec(), name, IndexKeyKind.Double),
            PropertyTypeFlags.String => new BTreeIndex<string>(tenant, new StringKeyCodec(), name, IndexKeyKind.String),
            PropertyTypeFlags.Bytes  => new BTreeIndex<byte[]>(tenant, new BytesKeyCodec(), name, IndexKeyKind.Bytes),
            _ => throw new CorruptionException(
                $"索引 '{name}' の PropertyTypeFlags={typeFlags} が materialize 対象外。"),
        };
        _indexes[name] = index;
        _indexFiles[name] = tenant;
    }

    private void PersistCatalog()
    {
        // 1. カタログを直列化する: tenantId(1) typeFlags(8) nameLen(2) nameBytes(可変)。
        var entries = new List<(byte TenantId, ulong TypeFlags, byte[] Name)>(_indexTenantIds.Count);
        int totalLen = 0;
        foreach (var (name, tenantId) in _indexTenantIds)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            ulong tf = (ulong)(_indexTypes.TryGetValue(name, out var f) ? f : PropertyTypeFlags.None);
            entries.Add((tenantId, tf, nameBytes));
            totalLen += 1 + 8 + 2 + nameBytes.Length;
        }

        byte[] blob = new byte[totalLen];
        int p = 0;
        foreach (var (tenantId, tf, nameBytes) in entries)
        {
            blob[p] = tenantId; p += 1;
            BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(p), tf); p += 8;
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(p), (ushort)nameBytes.Length); p += 2;
            nameBytes.CopyTo(blob.AsSpan(p)); p += nameBytes.Length;
        }

        // 2. 必要なテナント論理ページを確保する (header = 論理 1, data = 論理 2..)。
        int dataPages = (blob.Length + PagedFile.BodySize - 1) / PagedFile.BodySize;
        while (_catalogTenant.PageCount < 2 + dataPages)
            _catalogTenant.AllocatePage(PageKind.Header);

        // 3. header を書く。
        var wh = _catalogTenant.PinForWrite(new PageId(1));
        try
        {
            wh.Data[..(CatalogEntryCountOffset + 4)].Clear();
            BinaryPrimitives.WriteInt32LittleEndian(wh.Data[CatalogBlobLenOffset..], blob.Length);
            BinaryPrimitives.WriteInt32LittleEndian(wh.Data[CatalogEntryCountOffset..], entries.Count);
        }
        finally { wh.Dispose(); }

        // 4. ブロブを data ページへ書く。
        int off = 0;
        long dataPage = 2;
        while (off < blob.Length)
        {
            var dh = _catalogTenant.PinForWrite(new PageId(dataPage));
            try
            {
                int n = Math.Min(PagedFile.BodySize, blob.Length - off);
                blob.AsSpan(off, n).CopyTo(dh.Data);
            }
            finally { dh.Dispose(); }
            off += PagedFile.BodySize;
            dataPage++;
        }

        // 5. tx 外の DDL (SchemaApi.CreateIndex 等) ではカタログ更新が WAL に乗らないため、
        //    旧 .fileKinds/.idxmeta の即時 fsync と同等の durability を保つよう container を flush する。
        //    tx 内 (IndexInsert 経由の遅延作成) では PageImage が WAL に乗り commit/checkpoint で
        //    durable になるので flush しない (uncommitted ページの早期 steal を避ける)。
        if (WalPageContext.Current is null)
            _container.Flush();
    }

    public void Dispose()
    {
        foreach (var idx in _indexes.Values.OfType<IDisposable>())
            idx.Dispose();
        _indexes.Clear();
        _indexTypes.Clear();
        _indexFiles.Clear();
        _indexTenantIds.Clear();
        _usedTenantIds.Clear();
        if (_ownsContainer) _container.Dispose();
    }
}
