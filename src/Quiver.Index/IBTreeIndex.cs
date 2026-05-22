namespace Quiver.Index;

public interface IBTreeIndex<TKey> : IDisposable
{
    void Insert(in TKey key, long value);
    bool Delete(in TKey key, long value);
    BTreeValueEnumerator Seek(in TKey key);
    BTreeRangeEnumerator Range(in TKey from, bool fromInclusive, in TKey to, bool toInclusive);
    BTreeRangeEnumerator FullScan();
    int Height { get; }
    long EntryCount { get; }
    IEnumerable<long> SeekValues(TKey key);
    IEnumerable<long> RangeValues(TKey from, bool fromInclusive, TKey to, bool toInclusive);
    IEnumerable<long> AllValues();
}

public readonly ref struct KeyValueEntry
{
    private readonly ReadOnlySpan<byte> _keyBytes;
    private readonly long _value;

    public ReadOnlySpan<byte> KeyBytes => _keyBytes;
    public long Value => _value;

    internal KeyValueEntry(ReadOnlySpan<byte> keyBytes, long value)
    {
        _keyBytes = keyBytes; _value = value;
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

    /// <summary>
    /// PW-18 follow-up: スキーマ層から呼ばれ、(label, propertyKey) → indexName の対応を
    /// 登録する。これにより MergeNode が業務キー検索で自動的にインデックスを利用できる。
    /// 既定実装は no-op (バインディングを保持しないバックエンドはフルスキャン経路に落ちる)。
    /// </summary>
    void RegisterIndexBinding(string indexName, string label, string propertyKey) { }

    /// <summary>
    /// PW-18 follow-up: (label, propertyKey) に登録されたインデックス名を返す。
    /// バインディングが無い場合は <c>false</c> を返す (既定実装)。
    /// </summary>
    bool TryGetIndexName(string label, string propertyKey, out string indexName)
    {
        indexName = string.Empty;
        return false;
    }

    /// <summary>
    /// PW-18 follow-up: 登録済みインデックスのバインディングを列挙する
    /// (<see cref="ListIndexes"/> の補助。SchemaApi.ListIndexes のメタデータ復元に使う)。
    /// 既定実装は空シーケンス。
    /// </summary>
    IEnumerable<(string IndexName, string Label, string PropertyKey)> ListIndexBindings()
        => Array.Empty<(string, string, string)>();

    /// <summary>
    /// FT-17: エンコード済みキーで表現された 1 件のインデックス論理ミューテーションを
    /// 適用する。abort のインプロセス巻き戻しと、クラッシュ recovery の索引 undo パスが
    /// 共通で使う。既定実装は no-op (索引の永続性を持たないバックエンド向け)。
    /// </summary>
    void ApplyEncodedIndexMutation(
        string indexName, IndexKeyKind keyKind, ReadOnlySpan<byte> keyBytes,
        long value, bool isInsert) { }
}

public interface IBulkLoadable<TKey>
{
    void BulkLoad(IEnumerable<KeyValuePair<TKey, long>> sortedEntries);
}
