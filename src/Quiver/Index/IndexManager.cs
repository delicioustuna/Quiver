using System.Buffers.Binary;
using System.Text;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Storage;
using Quiver.Storage.Wal;
using Quiver.Text;

namespace Quiver.Index;

/// <summary>
/// 各 B+Tree 索引とその索引カタログ (name → tenantId / PropertyTypeFlags) を
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
    private const int CatalogBlobLenOffset = 0;   // int32: 直列化ブロブ長 (secondary + FT 両セクション合計)
    private const int CatalogEntryCountOffset = 4; // int32: secondary 索引件数
    private const int CatalogFtCountOffset = 8;    // int32: FTS-2 全文索引件数

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

    // FTS-2: 全文索引 (postings + norms の 2 テナント)。secondary 索引 (_indexes) とは別管理。
    // orphan sweep が tf/docLen を entityId と誤認しないよう _indexes には載せない。
    private readonly Dictionary<string, FullTextIndex> _ftIndexes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Label, string PropertyKey), string> _ftBindings = new();
    private readonly TokenizerRegistry _tokenizers = TokenizerRegistry.CreateDefault();

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
    /// テスト / ベンチ用。<paramref name="directory"/> 直下に
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
    /// 全索引のバッファプールダーティページを fsync する。
    /// <see cref="Quiver.Transactions.Checkpointer"/> がチェックポイント時に呼び、
    /// 索引内容を checkpointLsn 時点で durable にして WAL truncate を安全にする。
    /// 全索引は単一 container 上のテナントなので 1 回の flush で足りる。
    /// </summary>
    public void FlushAll()
    {
        if (_indexes.Count > 0 || _ftIndexes.Count > 0) _container.Flush();
    }

    /// <summary>
    /// abort の before-image undo がヘッダページを戻した後、全 B+Tree 索引
    /// (secondary + 全文 postings/norms) の in-memory ヘッダキャッシュを読み直す。
    /// </summary>
    public void ReloadAll()
    {
        foreach (var idx in _indexes.Values)
            if (idx is IBTreeIndexFlushable f) f.ReloadFromHeader();
        foreach (var ft in _ftIndexes.Values)
            ft.ReloadFromHeader();
    }

    /// <summary>
    /// 全 B+Tree 索引を走査し、<paramref name="isLive"/> が <c>false</c> を返した
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

        // FTS-2: 全文索引 (postings/norms) は entityId を key 側に持つので専用走査。
        // postings は key 末尾 8B、norms は key(Int64) が packed entityId。orphan は lane を
        // タグ付けして emit し、RemoveOrphans が postings/norms へ振り分ける。
        var int64 = new Int64KeyCodec();
        foreach (var ft in _ftIndexes.Values)
        {
            indexCount++;
            foreach (var kv in ft.EnumeratePostingsRaw())
            {
                entryCount++;
                if (!isLive(PostingsKey.DecodeEntityId(kv.Key)))
                    output.Add((ft.Name + FtLaneSep + PostingsLaneTag, kv.Key, kv.Value));
            }
            indexCount++;
            foreach (var kv in ft.EnumerateNormsRaw())
            {
                entryCount++;
                if (!isLive(int64.Decode(kv.Key)))
                    output.Add((ft.Name + FtLaneSep + NormsLaneTag, kv.Key, kv.Value));
            }
        }
        return (indexCount, entryCount);
    }

    // FTS-2: orphan の IndexName に埋める lane タグ。index 名に現れない制御文字で区切る。
    internal const char FtLaneSep = '';
    internal const string PostingsLaneTag = "postings";
    internal const string NormsLaneTag = "norms";

    /// <summary>
    /// 与えた orphan 一覧を索引から削除する。索引名で <see cref="_indexes"/> を引き、
    /// <see cref="IBTreeIndexFlushable.DeleteRawEntry"/> で生キー削除する。lane タグ付き名は
    /// 全文索引の postings/norms へ振り分ける。
    /// </summary>
    public int RemoveOrphans(IEnumerable<(string IndexName, byte[] RawKey, long Value)> orphans)
    {
        int removed = 0;
        foreach (var (name, key, value) in orphans)
        {
            int sep = name.IndexOf(FtLaneSep);
            if (sep >= 0)
            {
                var ftName = name[..sep];
                var lane = name[(sep + 1)..];
                if (_ftIndexes.TryGetValue(ftName, out var ft))
                {
                    bool ok = lane == PostingsLaneTag ? ft.DeletePostingsRaw(key, value) : ft.DeleteNormsRaw(key, value);
                    if (ok) removed++;
                }
                continue;
            }
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
    /// 索引名を <paramref name="oldName"/> から <paramref name="newName"/> へ変更する。
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
    /// 旧実装では索引ごとの <c>*.idx</c> PagedFile を snapshot へ列挙していたが、
    /// 索引は <c>graph.quiver</c> に同居するようになったため、snapshot の page-by-page コピーは
    /// container 物理ファイル (pageManager 経由) が一括カバーする。よって本プロパティは空を返す。
    /// </summary>
    public IEnumerable<IPagedFile> IndexFiles => Array.Empty<IPagedFile>();

    /// <summary>
    /// 指定索引の backing テナントの論理ページ数を返す。
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
    /// returns the <see cref="PropertyTypeFlags"/> the index was first
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
    // FTS-2: 全文索引 (postings + norms)
    // ------------------------------------------------------------------

    public FullTextIndex CreateFullTextIndex(string name, string label, string propertyKey, string tokenizerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentException.ThrowIfNullOrEmpty(propertyKey);
        ArgumentException.ThrowIfNullOrEmpty(tokenizerId);

        if (_ftIndexes.TryGetValue(name, out var existing))
        {
            if (!string.Equals(existing.TokenizerId, tokenizerId, StringComparison.Ordinal))
                throw new ConstraintException(
                    $"Full-text index '{name}' already exists with tokenizer '{existing.TokenizerId}'; " +
                    $"cannot recreate it with '{tokenizerId}'.");
            return existing;
        }
        if (_indexes.ContainsKey(name))
            throw new ConstraintException($"'{name}' already exists as a non-full-text index.");

        byte postingsTenant = AllocateTenantId(); _usedTenantIds.Add(postingsTenant);
        byte normsTenant = AllocateTenantId(); _usedTenantIds.Add(normsTenant);
        var ft = MaterializeFullText(name, label, propertyKey, tokenizerId, postingsTenant, normsTenant);
        PersistCatalog();
        return ft;
    }

    public bool TryGetFullTextIndex(string name, out FullTextIndex index)
        => _ftIndexes.TryGetValue(name, out index!);

    public bool TryGetFullTextIndexByLabelKey(string label, string propertyKey, out FullTextIndex index)
    {
        if (_ftBindings.TryGetValue((label, propertyKey), out var name)
            && _ftIndexes.TryGetValue(name, out index!))
            return true;
        index = null!;
        return false;
    }

    public IEnumerable<(string Name, string Label, string PropertyKey, string TokenizerId)> ListFullTextIndexes()
    {
        foreach (var ft in _ftIndexes.Values)
            yield return (ft.Name, ft.Label, ft.PropertyKey, ft.TokenizerId);
    }

    public bool DropFullTextIndex(string name)
    {
        if (!_ftIndexes.Remove(name, out var ft)) return false;
        ft.Dispose();
        _ftBindings.Remove((ft.Label, ft.PropertyKey));
        // テナント論理ページをグローバル free list へ回収する。
        if (_indexFiles.Remove(name + ":postings", out var pTenant)) pTenant.Truncate(1);
        if (_indexFiles.Remove(name + ":norms", out var nTenant)) nTenant.Truncate(1);
        _usedTenantIds.Remove(ft.PostingsTenantId);
        _usedTenantIds.Remove(ft.NormsTenantId);
        PersistCatalog();
        return true;
    }

    public ITokenizer ResolveTokenizer(string tokenizerId) => _tokenizers.Resolve(tokenizerId);

    public bool HasAnyFullTextIndex => _ftIndexes.Count > 0;

    public void MaintainFullText(FullTextIndex index, long entityId, string? oldText, string? newText)
    {
        var tok = _tokenizers.Resolve(index.TokenizerId);
        if (oldText is not null) index.RemoveDocument(entityId, tok, oldText);
        if (newText is not null) index.AddDocument(entityId, tok, newText);
    }

    // FTS-7: recovery 論理相 / abort 論理 undo の振り分け (spec: 07_fulltext.md#ft-recovery)。indexTenantId から
    // 該当 FullTextIndex (postings or norms tenant 一致) を引いて raw apply する。
    public void ApplyFtLeafRedo(byte tenantId, bool isUpsert, ReadOnlySpan<byte> key, long value)
    {
        if (TryGetFullTextByTenant(tenantId, out var ft)) ft.ApplyLeafRedo(tenantId, isUpsert, key, value);
    }

    public void ApplyFtLeafUndo(byte tenantId, bool isUpsert, ReadOnlySpan<byte> key, long value)
    {
        if (TryGetFullTextByTenant(tenantId, out var ft)) ft.ApplyLeafUndo(tenantId, isUpsert, key, value);
    }

    private bool TryGetFullTextByTenant(byte tenantId, out FullTextIndex ft)
    {
        foreach (var f in _ftIndexes.Values)
            if (f.PostingsTenantId == tenantId || f.NormsTenantId == tenantId) { ft = f; return true; }
        ft = null!;
        return false;
    }

    public void RegisterTokenizer(ITokenizer tokenizer) => _tokenizers.Register(tokenizer);

    private FullTextIndex MaterializeFullText(
        string name, string label, string propertyKey, string tokenizerId,
        byte postingsTenant, byte normsTenant)
    {
        var pTenant = _container.OpenTenant(postingsTenant, PageKind.Header);
        var nTenant = _container.OpenTenant(normsTenant, PageKind.Header);
        // FTS-7: postings/norms は logical-leaf モードで開く (spec: 07_fulltext.md#logical-wal,
        // leaf 更新 = FtLeafMutation 論理レコード、SMO = FtStructureImage)。logicalTenantId は recovery が tenant→tree を引くキー。
        var postings = new BTreeIndex<byte[]>(pTenant, new BytesKeyCodec(), name + ":postings", IndexKeyKind.Bytes,
            logicalLeaf: true, logicalTenantId: postingsTenant);
        var norms = new BTreeIndex<long>(nTenant, new Int64KeyCodec(), name + ":norms", IndexKeyKind.Int64,
            logicalLeaf: true, logicalTenantId: normsTenant);
        var ft = new FullTextIndex(name, label, propertyKey, tokenizerId, postingsTenant, normsTenant, postings, norms);
        _ftIndexes[name] = ft;
        _ftBindings[(label, propertyKey)] = name;
        // backing テナントを page-count 観測 / drop 時 truncate 用に登録する。
        _indexFiles[name + ":postings"] = pTenant;
        _indexFiles[name + ":norms"] = nTenant;
        _usedTenantIds.Add(postingsTenant);
        _usedTenantIds.Add(normsTenant);
        return ft;
    }

    // ------------------------------------------------------------------
    // 索引カタログ I/O (専用テナント上のブロブ)
    // ------------------------------------------------------------------

    private void LoadCatalogAndMaterialize()
    {
        long pageCount = _catalogTenant.PageCount;
        if (pageCount < 2) return; // header 未作成 = 索引ゼロ

        int blobLen, entryCount, ftCount;
        var hh = _catalogTenant.PinForRead(new PageId(1));
        try
        {
            blobLen = BinaryPrimitives.ReadInt32LittleEndian(hh.Data[CatalogBlobLenOffset..]);
            entryCount = BinaryPrimitives.ReadInt32LittleEndian(hh.Data[CatalogEntryCountOffset..]);
            ftCount = BinaryPrimitives.ReadInt32LittleEndian(hh.Data[CatalogFtCountOffset..]);
        }
        finally { hh.Dispose(); }
        if (blobLen <= 0) return;

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

        // FTS-2: 全文索引レコードセクション (secondary の直後)。
        for (int i = 0; i < ftCount; i++)
        {
            byte postingsTenant = blob[pos]; pos += 1;
            byte normsTenant = blob[pos]; pos += 1;
            string label = ReadString(blob, ref pos);
            string propKey = ReadString(blob, ref pos);
            string tokenizerId = ReadString(blob, ref pos);
            string name = ReadString(blob, ref pos);
            MaterializeFullText(name, label, propKey, tokenizerId, postingsTenant, normsTenant);
        }
    }

    private static string ReadString(byte[] blob, ref int pos)
    {
        int len = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos)); pos += 2;
        string s = Encoding.UTF8.GetString(blob, pos, len); pos += len;
        return s;
    }

    private static void WriteString(List<byte> dest, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Span<byte> lenBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)bytes.Length);
        dest.Add(lenBuf[0]);
        dest.Add(lenBuf[1]);
        dest.AddRange(bytes);
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
        // 1. カタログを直列化する。
        //    secondary セクション (索引件数=entryCount): tenantId(1) typeFlags(8) nameLen(2) nameBytes。
        //    FTS-2 全文索引セクション (件数=ftCount): postingsTenant(1) normsTenant(1)
        //       label(len+utf8) propKey(len+utf8) tokenizerId(len+utf8) name(len+utf8)。
        var blobList = new List<byte>();
        Span<byte> u64 = stackalloc byte[8];
        int entryCount = 0;
        foreach (var (name, tenantId) in _indexTenantIds)
        {
            ulong tf = (ulong)(_indexTypes.TryGetValue(name, out var f) ? f : PropertyTypeFlags.None);
            blobList.Add(tenantId);
            BinaryPrimitives.WriteUInt64LittleEndian(u64, tf);
            for (int i = 0; i < 8; i++) blobList.Add(u64[i]);
            WriteString(blobList, name);
            entryCount++;
        }
        int ftCount = 0;
        foreach (var ft in _ftIndexes.Values)
        {
            blobList.Add(ft.PostingsTenantId);
            blobList.Add(ft.NormsTenantId);
            WriteString(blobList, ft.Label);
            WriteString(blobList, ft.PropertyKey);
            WriteString(blobList, ft.TokenizerId);
            WriteString(blobList, ft.Name);
            ftCount++;
        }
        byte[] blob = blobList.ToArray();

        // 2. 必要なテナント論理ページを確保する (header = 論理 1, data = 論理 2..)。
        int dataPages = (blob.Length + PagedFile.BodySize - 1) / PagedFile.BodySize;
        while (_catalogTenant.PageCount < 2 + dataPages)
            _catalogTenant.AllocatePage(PageKind.Header);

        // 3. header を書く。
        var wh = _catalogTenant.PinForWrite(new PageId(1));
        try
        {
            wh.Data[..(CatalogFtCountOffset + 4)].Clear();
            BinaryPrimitives.WriteInt32LittleEndian(wh.Data[CatalogBlobLenOffset..], blob.Length);
            BinaryPrimitives.WriteInt32LittleEndian(wh.Data[CatalogEntryCountOffset..], entryCount);
            BinaryPrimitives.WriteInt32LittleEndian(wh.Data[CatalogFtCountOffset..], ftCount);
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
        foreach (var ft in _ftIndexes.Values)
            ft.Dispose();
        _indexes.Clear();
        _indexTypes.Clear();
        _indexFiles.Clear();
        _indexTenantIds.Clear();
        _ftIndexes.Clear();
        _ftBindings.Clear();
        _usedTenantIds.Clear();
        if (_ownsContainer) _container.Dispose();
    }
}
