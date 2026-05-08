using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Index;

public sealed class IndexManager : IIndexManager, IDisposable
{
    private readonly string _directory;
    private readonly Dictionary<string, object> _indexes = new(StringComparer.Ordinal);

    public IndexManager(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public IBTreeIndex<int>    CreateInt32Index(string name)  => GetOrCreate(name, new Int32KeyCodec());
    public IBTreeIndex<long>   CreateInt64Index(string name)  => GetOrCreate(name, new Int64KeyCodec());
    public IBTreeIndex<double> CreateDoubleIndex(string name) => GetOrCreate(name, new DoubleKeyCodec());
    public IBTreeIndex<string> CreateStringIndex(string name) => GetOrCreate(name, new StringKeyCodec());
    public IBTreeIndex<byte[]> CreateBytesIndex(string name)  => GetOrCreate(name, new BytesKeyCodec());

    public bool DropIndex(string name)
    {
        if (!_indexes.TryGetValue(name, out var idx)) return false;
        (idx as IDisposable)?.Dispose();
        _indexes.Remove(name);
        var path = IndexPath(name);
        if (File.Exists(path)) File.Delete(path);
        return true;
    }

    public IEnumerable<string> ListIndexes() => _indexes.Keys;

    private IBTreeIndex<TKey> GetOrCreate<TKey>(string name, IKeyCodec<TKey> codec)
    {
        if (_indexes.TryGetValue(name, out var existing)) return (IBTreeIndex<TKey>)existing;
        var index = new BTreeIndex<TKey>(new PagedFile(IndexPath(name)), codec);
        _indexes[name] = index;
        return index;
    }

    private string IndexPath(string name) => Path.Combine(_directory, $"{name}.idx");

    public void Dispose()
    {
        foreach (var idx in _indexes.Values.OfType<IDisposable>())
            idx.Dispose();
        _indexes.Clear();
    }
}
