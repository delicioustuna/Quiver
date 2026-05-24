using Quiver.Core;
using Quiver.Storage;
using Quiver.Wal;

namespace Quiver.Index;

public sealed class IndexManager : IIndexManager, IDisposable
{
    private readonly string _directory;
    // FT-19: null でない場合、各索引 PagedFile に EnableWalLogging を呼んで物理 PageImage /
    // before-image (CLR) を WAL に流す。data ファイルと同じ ARIES regime に乗せて
    // partial-split を含む構造破綻に対する自動復旧を可能にする。
    private readonly IWriteAheadLog? _wal;
    // FT-19: WAL 有効時のみ。索引名 → fileKind の永続マッピング。動的に増減する索引の
    // fileKind を予約レンジ (0x40..0xFF) から割り当てる。
    private readonly FileKindCatalog? _catalog;
    // FT-19: WAL 有効時のみ。新規索引作成時に新しい PagedFile を fileRegistry へ登録する経路。
    // RecoveryManager / AbortUndoHandler / Checkpointer が透過的に索引を扱えるようにする。
    private readonly IDictionary<byte, IPagedFile>? _runtimeFileRegistry;
    private readonly Dictionary<string, object> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyTypeFlags> _indexTypes = new(StringComparer.Ordinal);
    // PW-18 follow-up: (label, propertyKey) → indexName のバインディング。
    // SchemaApi.CreateIndex から登録され、MergeNode の自動インデックス選択に使われる。
    private readonly Dictionary<(string Label, string PropertyKey), string> _bindings
        = new();
    private readonly Dictionary<string, (string Label, string PropertyKey)> _bindingByName
        = new(StringComparer.Ordinal);

    public IndexManager(string directory) : this(directory, wal: null, runtimeFileRegistry: null) { }

    public IndexManager(string directory, IWriteAheadLog? wal)
        : this(directory, wal, runtimeFileRegistry: null) { }

    public IndexManager(
        string directory,
        IWriteAheadLog? wal,
        IDictionary<byte, IPagedFile>? runtimeFileRegistry)
    {
        _directory = directory;
        _wal = wal;
        _runtimeFileRegistry = runtimeFileRegistry;
        Directory.CreateDirectory(directory);
        // FT-19: WAL 有効時のみ catalog を初期化する。WAL なしの経路 (tests, SQLite backend)
        // では fileKind の割り当てが不要なので catalog ファイルも作らない。
        _catalog = wal != null ? new FileKindCatalog(directory) : null;
    }

    public IBTreeIndex<int>    CreateInt32Index(string name)  => GetOrCreate(name, new Int32KeyCodec(),  PropertyTypeFlags.Int32,  IndexKeyKind.Int32);
    public IBTreeIndex<long>   CreateInt64Index(string name)  => GetOrCreate(name, new Int64KeyCodec(),  PropertyTypeFlags.Int64,  IndexKeyKind.Int64);
    public IBTreeIndex<double> CreateDoubleIndex(string name) => GetOrCreate(name, new DoubleKeyCodec(), PropertyTypeFlags.Double, IndexKeyKind.Double);
    public IBTreeIndex<string> CreateStringIndex(string name) => GetOrCreate(name, new StringKeyCodec(), PropertyTypeFlags.String, IndexKeyKind.String);
    public IBTreeIndex<byte[]> CreateBytesIndex(string name)  => GetOrCreate(name, new BytesKeyCodec(),  PropertyTypeFlags.Bytes,  IndexKeyKind.Bytes);

    /// <summary>
    /// FT-18: 全索引のバッファプールダーティページを fsync する。
    /// <see cref="Quiver.Transactions.Checkpointer"/> がチェックポイント時に呼び、
    /// 索引ファイル内容を checkpointLsn 時点で durable にして WAL truncate を安全にする。
    /// </summary>
    public void FlushAll()
    {
        foreach (var idx in _indexes.Values)
            if (idx is IBTreeIndexFlushable f) f.Flush();
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
    /// 索引が見つからない / 既に削除済みのエントリはスキップする (戻り値はカウントしない)。
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
        // FT-19: catalog 引きで fileKind を取り、fileRegistry からも除去する。
        // 索引 Dispose 前に取り出さないと _indexes から消えた後で参照できない。
        byte? fileKindToFree = null;
        if (_catalog != null && _catalog.TryGet(name, out byte k)) fileKindToFree = k;

        (idx as IDisposable)?.Dispose();
        _indexes.Remove(name);
        _indexTypes.Remove(name);
        if (_bindingByName.TryGetValue(name, out var key))
        {
            _bindings.Remove(key);
            _bindingByName.Remove(name);
        }
        if (fileKindToFree is byte freedKind)
        {
            _runtimeFileRegistry?.Remove(freedKind);
            _catalog?.Remove(name);
        }
        var path = IndexPath(name);
        if (File.Exists(path)) File.Delete(path);
        var meta = MetaPath(name);
        if (File.Exists(meta)) File.Delete(meta);
        return true;
    }

    public IEnumerable<string> ListIndexes() => _indexes.Keys;

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
            // BA-8: an index file is single-type. Re-opening with a different key
            // type would corrupt the B+ tree, so fail fast instead of silently
            // mixing numeric and string entries.
            if (_indexTypes.TryGetValue(name, out var stored) && stored != typeFlag)
                throw new ConstraintException(
                    $"Index '{name}' was created as {stored}; cannot reopen it as {typeFlag}.");
            return (IBTreeIndex<TKey>)existing;
        }

        // Persisted type flag survives restarts so a different process can't
        // accidentally open a String index as Int64.
        var metaPath = MetaPath(name);
        if (File.Exists(metaPath))
        {
            var bytes = File.ReadAllBytes(metaPath);
            if (bytes.Length >= 8)
            {
                var persisted = (PropertyTypeFlags)BitConverter.ToUInt64(bytes, 0);
                if (persisted != typeFlag)
                    throw new ConstraintException(
                        $"Index '{name}' on disk is {persisted}; cannot open it as {typeFlag}.");
            }
        }
        else
        {
            File.WriteAllBytes(metaPath, BitConverter.GetBytes((ulong)typeFlag));
        }

        var pagedFile = new PagedFile(IndexPath(name));
        // FT-19: 索引ファイルを ARIES 物理 page-WAL ロギング対象にする。
        // PinForWrite で before-image (CLR) を捕捉し、UnpinDirty で PageImage を蓄積する。
        // partial-split を含む全構造破綻シナリオが PageImage redo + CLR undo で自動復旧可能になる。
        if (_wal != null && _catalog != null)
        {
            byte fileKind = _catalog.GetOrAllocate(name);
            pagedFile.EnableWalLogging(fileKind, _wal);
            _runtimeFileRegistry?[fileKind] = pagedFile;
        }
        var index = new BTreeIndex<TKey>(pagedFile, codec, name, kind);
        _indexes[name] = index;
        _indexTypes[name] = typeFlag;
        return index;
    }

    /// <summary>
    /// FT-19: 既存索引ファイルを recovery 前に全 open し、各々 WAL ロギング対象として
    /// <paramref name="fileRegistry"/> に登録する。<see cref="Quiver.Wal.WalRecordType.PageImage"/>
    /// / <see cref="Quiver.Wal.WalRecordType.CompensationLogRecord"/> の replay 経路は
    /// fileRegistry で fileKind から <see cref="IPagedFile"/> を引いてページを書くため、
    /// recovery 開始時点で索引ファイルが登録されていないと redo が落ちる。
    ///
    /// catalog 由来の (name, fileKind) ペアを順に走査し、.idxmeta の PropertyTypeFlags で
    /// TKey をディスパッチして <see cref="BTreeIndex{TKey}"/> を materialize する。
    /// </summary>
    public void MaterializeAll(IDictionary<byte, IPagedFile> fileRegistry)
    {
        ArgumentNullException.ThrowIfNull(fileRegistry);
        if (_wal == null || _catalog == null) return;

        foreach (var (name, fileKind) in _catalog.Entries)
        {
            var idxPath = IndexPath(name);
            if (!File.Exists(idxPath))
                throw new CorruptionException(
                    $"catalog が索引 '{name}' (fileKind {fileKind:X2}) を参照しているが " +
                    $"{idxPath} が存在しない。");
            var metaPath = MetaPath(name);
            if (!File.Exists(metaPath))
                throw new CorruptionException($"索引 '{name}' の .idxmeta が存在しない。");

            var metaBytes = File.ReadAllBytes(metaPath);
            if (metaBytes.Length < 8)
                throw new CorruptionException($"索引 '{name}' の .idxmeta が破損 (size {metaBytes.Length}).");
            var flags = (PropertyTypeFlags)BitConverter.ToUInt64(metaBytes, 0);

            switch (flags)
            {
                case PropertyTypeFlags.Int32:
                    MaterializeOne(name, fileKind, new Int32KeyCodec(), flags, IndexKeyKind.Int32, fileRegistry);
                    break;
                case PropertyTypeFlags.Int64:
                    MaterializeOne(name, fileKind, new Int64KeyCodec(), flags, IndexKeyKind.Int64, fileRegistry);
                    break;
                case PropertyTypeFlags.Double:
                    MaterializeOne(name, fileKind, new DoubleKeyCodec(), flags, IndexKeyKind.Double, fileRegistry);
                    break;
                case PropertyTypeFlags.String:
                    MaterializeOne(name, fileKind, new StringKeyCodec(), flags, IndexKeyKind.String, fileRegistry);
                    break;
                case PropertyTypeFlags.Bytes:
                    MaterializeOne(name, fileKind, new BytesKeyCodec(), flags, IndexKeyKind.Bytes, fileRegistry);
                    break;
                default:
                    throw new CorruptionException(
                        $"索引 '{name}' の PropertyTypeFlags={flags} が materialize 対象外。");
            }
        }
    }

    private void MaterializeOne<TKey>(
        string name, byte fileKind, IKeyCodec<TKey> codec,
        PropertyTypeFlags typeFlag, IndexKeyKind kind,
        IDictionary<byte, IPagedFile> fileRegistry)
    {
        var pagedFile = new PagedFile(IndexPath(name));
        pagedFile.EnableWalLogging(fileKind, _wal!);
        fileRegistry[fileKind] = pagedFile;

        var index = new BTreeIndex<TKey>(pagedFile, codec, name, kind);
        _indexes[name] = index;
        _indexTypes[name] = typeFlag;
    }

    private string IndexPath(string name) => Path.Combine(_directory, $"{name}.idx");
    private string MetaPath(string name)  => Path.Combine(_directory, $"{name}.idxmeta");

    public void Dispose()
    {
        foreach (var idx in _indexes.Values.OfType<IDisposable>())
            idx.Dispose();
        _indexes.Clear();
        _indexTypes.Clear();
    }
}
