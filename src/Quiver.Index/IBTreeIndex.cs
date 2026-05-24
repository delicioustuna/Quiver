namespace Quiver.Index;

public interface IBTreeIndex<TKey> : IDisposable, IBTreeIndexFlushable
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

/// <summary>
/// FT-18: 型パラメータに依存しない、索引の永続化フラッシュ用インタフェース。
/// <see cref="Checkpointer"/> が型を意識せずに <see cref="IIndexManager.FlushAll"/>
/// 経由で全索引を fsync するために使う。
/// FT-22 では orphan GC が型を意識せず全索引を走査するため、生キー / 生エントリ操作も
/// この non-generic 経路に乗せる。
/// </summary>
public interface IBTreeIndexFlushable
{
    /// <summary>索引バッファプールのダーティページを fsync する。</summary>
    void Flush();

    /// <summary>
    /// FT-22: 索引内の全 (生キー, 値) ペアを leaf 順に列挙する。
    /// 値は <see cref="Quiver.Core.NodeId.Value"/> など long を想定。
    /// orphan 検出は呼び出し側 (IndexManager.ValidateAll) で行う。
    /// </summary>
    IEnumerable<KeyValuePair<byte[], long>> EnumerateRawEntries();

    /// <summary>
    /// FT-22: 与えた (生キー, 値) ペアが索引に存在すれば削除する。
    /// orphan repair で <see cref="EnumerateRawEntries"/> から拾った生キーを
    /// そのまま使えるよう、コーデック経由のエンコードをスキップする経路。
    /// </summary>
    /// <returns>削除に成功したら <c>true</c>、ペアが見つからなければ <c>false</c>。</returns>
    bool DeleteRawEntry(ReadOnlySpan<byte> rawKey, long value);
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
    /// FT-18: 管理下の全索引ファイルのバッファプールダーティページを fsync する。
    /// <see cref="Quiver.Transactions.Checkpointer"/> がチェックポイント時にデータファイル
    /// と並べて呼ぶ。これにより WAL truncate 後も索引内容が durable に残り、復旧時の
    /// 冪等 redo の起点として有効な状態が保証される。既定実装は no-op。
    /// </summary>
    void FlushAll() { }

    /// <summary>
    /// FT-22: 全 B+Tree 索引を走査し、<paramref name="isLive"/> が <c>false</c> を返した
    /// 値 (NodeId.Value 互換) を持つエントリを orphan として収集する。
    /// 戻り値の <c>EntryCount</c> は走査総数、<c>IndexCount</c> は走査対象の索引数。
    /// 既定実装は何もせず (0, 0) を返す。
    /// </summary>
    (int IndexCount, long EntryCount) CollectOrphans(
        Func<long, bool> isLive,
        ICollection<(string IndexName, byte[] RawKey, long Value)> output)
        => (0, 0);

    /// <summary>
    /// FT-22: <see cref="CollectOrphans"/> で得た一覧を実際に索引から削除する。
    /// 削除に成功した件数を返す。索引が dispose 済み等で見つからないエントリはスキップ。
    /// 既定実装は no-op。
    /// </summary>
    int RemoveOrphans(IEnumerable<(string IndexName, byte[] RawKey, long Value)> orphans) => 0;
}

public interface IBulkLoadable<TKey>
{
    void BulkLoad(IEnumerable<KeyValuePair<TKey, long>> sortedEntries);
}
