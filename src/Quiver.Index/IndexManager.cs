using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Index;

public sealed class IndexManager : IIndexManager, IDisposable
{
    private readonly string _directory;
    private readonly Dictionary<string, object> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyTypeFlags> _indexTypes = new(StringComparer.Ordinal);

    public IndexManager(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public IBTreeIndex<int>    CreateInt32Index(string name)  => GetOrCreate(name, new Int32KeyCodec(),  PropertyTypeFlags.Int32);
    public IBTreeIndex<long>   CreateInt64Index(string name)  => GetOrCreate(name, new Int64KeyCodec(),  PropertyTypeFlags.Int64);
    public IBTreeIndex<double> CreateDoubleIndex(string name) => GetOrCreate(name, new DoubleKeyCodec(), PropertyTypeFlags.Double);
    public IBTreeIndex<string> CreateStringIndex(string name) => GetOrCreate(name, new StringKeyCodec(), PropertyTypeFlags.String);
    public IBTreeIndex<byte[]> CreateBytesIndex(string name)  => GetOrCreate(name, new BytesKeyCodec(),  PropertyTypeFlags.Bytes);

    public bool DropIndex(string name)
    {
        if (!_indexes.TryGetValue(name, out var idx)) return false;
        (idx as IDisposable)?.Dispose();
        _indexes.Remove(name);
        _indexTypes.Remove(name);
        var path = IndexPath(name);
        if (File.Exists(path)) File.Delete(path);
        var meta = MetaPath(name);
        if (File.Exists(meta)) File.Delete(meta);
        return true;
    }

    public IEnumerable<string> ListIndexes() => _indexes.Keys;

    /// <summary>
    /// BA-8: returns the <see cref="PropertyTypeFlags"/> the index was first
    /// registered with, or <see cref="PropertyTypeFlags.None"/> if the index
    /// has not been created yet.
    /// </summary>
    public PropertyTypeFlags GetIndexTypeFlags(string name)
        => _indexTypes.TryGetValue(name, out var f) ? f : PropertyTypeFlags.None;

    private IBTreeIndex<TKey> GetOrCreate<TKey>(string name, IKeyCodec<TKey> codec, PropertyTypeFlags typeFlag)
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

        var index = new BTreeIndex<TKey>(new PagedFile(IndexPath(name)), codec);
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
