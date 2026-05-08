using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Index;

internal sealed class BTreeIndex<TKey> : IBTreeIndex<TKey>
{
    private readonly IPagedFile _file;

    public BTreeIndex(IPagedFile file)
    {
        _file = file;
    }

    public int Height => throw new NotImplementedException();
    public long EntryCount => throw new NotImplementedException();

    public void Insert(in TKey key, long value) => throw new NotImplementedException();
    public bool Delete(in TKey key, long value) => throw new NotImplementedException();
    public BTreeValueEnumerator Seek(in TKey key) => throw new NotImplementedException();
    public BTreeRangeEnumerator Range(in TKey from, bool fromInclusive, in TKey to, bool toInclusive) => throw new NotImplementedException();
    public BTreeRangeEnumerator FullScan() => throw new NotImplementedException();
    public void Dispose() { }
}

internal sealed class IndexManager : IIndexManager
{
    private readonly string _directory;

    public IndexManager(string directory)
    {
        _directory = directory;
    }

    public IBTreeIndex<int> CreateInt32Index(string name) => throw new NotImplementedException();
    public IBTreeIndex<long> CreateInt64Index(string name) => throw new NotImplementedException();
    public IBTreeIndex<double> CreateDoubleIndex(string name) => throw new NotImplementedException();
    public IBTreeIndex<string> CreateStringIndex(string name) => throw new NotImplementedException();
    public IBTreeIndex<byte[]> CreateBytesIndex(string name) => throw new NotImplementedException();
    public bool DropIndex(string name) => throw new NotImplementedException();
    public IEnumerable<string> ListIndexes() => throw new NotImplementedException();
}
