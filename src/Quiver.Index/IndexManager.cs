using Quiver.Core;
using Quiver.Storage;
using Quiver.Wal;

namespace Quiver.Index;

public sealed class IndexManager : IIndexManager, IDisposable
{
    private readonly string _directory;
    // FT-18: null でない場合、各索引 PagedFile に EnableWalFlushOnly を呼び、
    // buffer-pool eviction の write-ahead 順序を効かせる (gap 2 を FT-15 同パリティへ)。
    private readonly IWriteAheadLog? _wal;
    private readonly Dictionary<string, object> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyTypeFlags> _indexTypes = new(StringComparer.Ordinal);
    // PW-18 follow-up: (label, propertyKey) → indexName のバインディング。
    // SchemaApi.CreateIndex から登録され、MergeNode の自動インデックス選択に使われる。
    private readonly Dictionary<(string Label, string PropertyKey), string> _bindings
        = new();
    private readonly Dictionary<string, (string Label, string PropertyKey)> _bindingByName
        = new(StringComparer.Ordinal);

    public IndexManager(string directory) : this(directory, wal: null) { }

    public IndexManager(string directory, IWriteAheadLog? wal)
    {
        _directory = directory;
        _wal = wal;
        Directory.CreateDirectory(directory);
    }

    public IBTreeIndex<int>    CreateInt32Index(string name)  => GetOrCreate(name, new Int32KeyCodec(),  PropertyTypeFlags.Int32,  IndexKeyKind.Int32);
    public IBTreeIndex<long>   CreateInt64Index(string name)  => GetOrCreate(name, new Int64KeyCodec(),  PropertyTypeFlags.Int64,  IndexKeyKind.Int64);
    public IBTreeIndex<double> CreateDoubleIndex(string name) => GetOrCreate(name, new DoubleKeyCodec(), PropertyTypeFlags.Double, IndexKeyKind.Double);
    public IBTreeIndex<string> CreateStringIndex(string name) => GetOrCreate(name, new StringKeyCodec(), PropertyTypeFlags.String, IndexKeyKind.String);
    public IBTreeIndex<byte[]> CreateBytesIndex(string name)  => GetOrCreate(name, new BytesKeyCodec(),  PropertyTypeFlags.Bytes,  IndexKeyKind.Bytes);

    /// <summary>
    /// FT-17 / FT-18: 索引論理ミューテーションの<strong>冪等</strong>適用。recovery の
    /// undo / redo パスと、in-process abort 巻き戻しの両方から呼ばれる。
    /// <c>(key, value)</c> ペアの存在を検査してから Insert / Delete するので、
    /// 二重適用でも <see cref="IBTreeIndex{TKey}.EntryCount"/> が破綻しない。
    /// <see cref="IndexUndoContext"/> へは通知しない (recovery / abort いずれの呼出元でも
    /// ambient context は事前にクリア済み)。
    /// </summary>
    public void ApplyEncodedIndexMutation(
        string indexName, IndexKeyKind keyKind, ReadOnlySpan<byte> keyBytes,
        long value, bool isInsert)
    {
        switch (keyKind)
        {
            case IndexKeyKind.Int32:
            {
                var idx = CreateInt32Index(indexName);
                int k = new Int32KeyCodec().Decode(keyBytes);
                if (isInsert) idx.InsertIfAbsent(k, value); else idx.DeleteIfPresent(k, value);
                break;
            }
            case IndexKeyKind.Int64:
            {
                var idx = CreateInt64Index(indexName);
                long k = new Int64KeyCodec().Decode(keyBytes);
                if (isInsert) idx.InsertIfAbsent(k, value); else idx.DeleteIfPresent(k, value);
                break;
            }
            case IndexKeyKind.Double:
            {
                var idx = CreateDoubleIndex(indexName);
                double k = new DoubleKeyCodec().Decode(keyBytes);
                if (isInsert) idx.InsertIfAbsent(k, value); else idx.DeleteIfPresent(k, value);
                break;
            }
            case IndexKeyKind.String:
            {
                var idx = CreateStringIndex(indexName);
                string k = new StringKeyCodec().Decode(keyBytes);
                if (isInsert) idx.InsertIfAbsent(k, value); else idx.DeleteIfPresent(k, value);
                break;
            }
            case IndexKeyKind.Bytes:
            {
                var idx = CreateBytesIndex(indexName);
                byte[] k = new BytesKeyCodec().Decode(keyBytes);
                if (isInsert) idx.InsertIfAbsent(k, value); else idx.DeleteIfPresent(k, value);
                break;
            }
        }
    }

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

    public bool DropIndex(string name)
    {
        if (!_indexes.TryGetValue(name, out var idx)) return false;
        (idx as IDisposable)?.Dispose();
        _indexes.Remove(name);
        _indexTypes.Remove(name);
        if (_bindingByName.TryGetValue(name, out var key))
        {
            _bindings.Remove(key);
            _bindingByName.Remove(name);
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
        // FT-18: 索引ファイルにも write-ahead 順序を効かせる。物理 PageImage は出さず、
        // buffer-pool eviction 前に WAL を flush して IndexMutation の durability を確保する。
        if (_wal != null) pagedFile.EnableWalFlushOnly(_wal);
        var index = new BTreeIndex<TKey>(pagedFile, codec, name, kind);
        _indexes[name] = index;
        _indexTypes[name] = typeFlag;
        return index;
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
