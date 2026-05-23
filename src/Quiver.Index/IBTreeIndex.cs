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

    /// <summary>
    /// FT-18: 同一 <c>(key, value)</c> ペアが既に存在しなければ Insert する。
    /// 索引 redo / undo の冪等再生で使う (crash recovery と in-process abort の双方)。
    /// 存在判定にヒットした場合はエントリ数も <see cref="IndexUndoContext"/> も更新しない。
    /// <strong>前提</strong>: 本インデックスでは <c>(key, value)</c> ペアが論理的に一意である
    /// (プロパティ/ラベル索引で成立)。
    /// </summary>
    /// <returns>実際に挿入した場合 <c>true</c>、既に存在し no-op だった場合 <c>false</c>。</returns>
    bool InsertIfAbsent(in TKey key, long value);

    /// <summary>
    /// FT-18: <c>(key, value)</c> ペアが存在すれば Delete する。
    /// 索引 redo / undo の冪等再生で使う。<see cref="Delete"/> と異なり、
    /// <see cref="IndexUndoContext"/> へ論理ミューテーションを記録しない。
    /// </summary>
    /// <returns>実際に削除した場合 <c>true</c>、不在で no-op だった場合 <c>false</c>。</returns>
    bool DeleteIfPresent(in TKey key, long value);
}

/// <summary>
/// FT-18: 型パラメータに依存しない、索引の永続化フラッシュ用インタフェース。
/// <see cref="Checkpointer"/> が型を意識せずに <see cref="IIndexManager.FlushAll"/>
/// 経由で全索引を fsync するために使う。
/// </summary>
public interface IBTreeIndexFlushable
{
    /// <summary>索引バッファプールのダーティページを fsync する。</summary>
    void Flush();
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
    /// FT-17 / FT-18: エンコード済みキーで表現された 1 件のインデックス論理ミューテーションを
    /// <strong>冪等に</strong>適用する。abort のインプロセス巻き戻しと、クラッシュ recovery の
    /// 索引 undo / redo パスが共通で使う。既存エントリと一致する Insert、不在エントリへの
    /// Delete はいずれも no-op になり、二重適用しても <see cref="IBTreeIndex{TKey}.EntryCount"/>
    /// が壊れない。<see cref="IndexUndoContext"/> への論理ミューテーション通知も発生しない。
    /// 既定実装は no-op (索引の永続性を持たないバックエンド向け)。
    /// </summary>
    void ApplyEncodedIndexMutation(
        string indexName, IndexKeyKind keyKind, ReadOnlySpan<byte> keyBytes,
        long value, bool isInsert) { }

    /// <summary>
    /// FT-18: 管理下の全索引ファイルのバッファプールダーティページを fsync する。
    /// <see cref="Quiver.Transactions.Checkpointer"/> がチェックポイント時にデータファイル
    /// と並べて呼ぶ。これにより WAL truncate 後も索引内容が durable に残り、復旧時の
    /// 冪等 redo の起点として有効な状態が保証される。既定実装は no-op。
    /// </summary>
    void FlushAll() { }
}

public interface IBulkLoadable<TKey>
{
    void BulkLoad(IEnumerable<KeyValuePair<TKey, long>> sortedEntries);
}
