namespace GraphDb.Engine.Index;

public interface IBTreeIndex<TKey> : IDisposable
{
    void Insert(in TKey key, long value);
    bool Delete(in TKey key, long value);
    BTreeValueEnumerator Seek(in TKey key);
    BTreeRangeEnumerator Range(in TKey from, bool fromInclusive, in TKey to, bool toInclusive);
    BTreeRangeEnumerator FullScan();
    int Height { get; }
    long EntryCount { get; }
}

public ref struct BTreeValueEnumerator
{
    public bool MoveNext() => throw new NotImplementedException();
    public long Current => throw new NotImplementedException();
    public void Dispose() { }
}

public ref struct BTreeRangeEnumerator
{
    public bool MoveNext() => throw new NotImplementedException();
    public KeyValueEntry Current => throw new NotImplementedException();
    public void Dispose() { }
}

public readonly ref struct KeyValueEntry
{
    private readonly ReadOnlySpan<byte> _keyBytes;
    private readonly long _value;

    public ReadOnlySpan<byte> KeyBytes => _keyBytes;
    public long Value => _value;

    internal KeyValueEntry(ReadOnlySpan<byte> keyBytes, long value)
    {
        _keyBytes = keyBytes;
        _value = value;
    }
}

public interface IIndexManager
{
    IBTreeIndex<int> CreateInt32Index(string name);
    IBTreeIndex<long> CreateInt64Index(string name);
    IBTreeIndex<double> CreateDoubleIndex(string name);
    IBTreeIndex<string> CreateStringIndex(string name);
    IBTreeIndex<byte[]> CreateBytesIndex(string name);
    bool DropIndex(string name);
    IEnumerable<string> ListIndexes();
}

public interface IBulkLoadable<TKey>
{
    void BulkLoad(IEnumerable<KeyValuePair<TKey, long>> sortedEntries);
}
