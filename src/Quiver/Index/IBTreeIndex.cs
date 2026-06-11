using Quiver.Index.FullText;
using Quiver.Text;

namespace Quiver.Index;

internal interface IBTreeIndex<TKey> : IDisposable, IBTreeIndexFlushable
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
internal interface IBTreeIndexFlushable
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

internal readonly ref struct KeyValueEntry
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

internal interface IIndexManager
{
    IBTreeIndex<int> CreateInt32Index(string name);
    IBTreeIndex<long> CreateInt64Index(string name);
    IBTreeIndex<double> CreateDoubleIndex(string name);
    IBTreeIndex<string> CreateStringIndex(string name);
    IBTreeIndex<byte[]> CreateBytesIndex(string name);
    bool DropIndex(string name);
    IEnumerable<string> ListIndexes();

    /// <summary>
    /// OP-4 / ARCH-4: 索引を <paramref name="oldName"/> から <paramref name="newName"/> へリネームする。
    /// 索引はテナント ID で識別されるため、テナント / B+Tree 実体・ページ・WAL 意味はすべて不変で、
    /// カタログ上の name キーと (label, propertyKey) バインディングだけが追従する。
    /// 旧名が見つからない場合は <c>false</c> を返す (冪等)。
    /// 既定実装は <see cref="NotSupportedException"/>。
    /// </summary>
    bool RenameIndex(string oldName, string newName)
        => throw new NotSupportedException("RenameIndex is not supported by this index manager.");

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

    // ---- FTS-2: full-text indexes (postings + norms tenants) ----

    /// <summary>
    /// FTS-2: 全文索引 (postings + norms の 2 テナント) を作成する。既存なら既存を返す。
    /// 既定実装は <see cref="NotSupportedException"/> (binary backend のみ対応)。
    /// </summary>
    FullTextIndex CreateFullTextIndex(string name, string label, string propertyKey, string tokenizerId)
        => throw new NotSupportedException("Full-text indexes are not supported by this index manager.");

    /// <summary>FTS-2: 名前で全文索引を引く。既定実装は false。</summary>
    bool TryGetFullTextIndex(string name, out FullTextIndex index)
    {
        index = null!;
        return false;
    }

    /// <summary>
    /// FTS-2: (label, propertyKey) に bound された全文索引を引く (透過維持フックの探索用)。
    /// 既定実装は false。
    /// </summary>
    bool TryGetFullTextIndexByLabelKey(string label, string propertyKey, out FullTextIndex index)
    {
        index = null!;
        return false;
    }

    /// <summary>FTS-2: 登録済み全文索引のメタを列挙する。既定実装は空。</summary>
    IEnumerable<(string Name, string Label, string PropertyKey, string TokenizerId)> ListFullTextIndexes()
        => Array.Empty<(string, string, string, string)>();

    /// <summary>FTS-2: 全文索引を削除する。既定実装は false。</summary>
    bool DropFullTextIndex(string name) => false;

    /// <summary>FTS-2: tokenizerId からトークナイザを解決する (catalog 記録値を registry 経由で)。</summary>
    ITokenizer ResolveTokenizer(string tokenizerId)
        => throw new NotSupportedException("This index manager has no tokenizer registry.");
}

internal interface IBulkLoadable<TKey>
{
    void BulkLoad(IEnumerable<KeyValuePair<TKey, long>> sortedEntries);
}
