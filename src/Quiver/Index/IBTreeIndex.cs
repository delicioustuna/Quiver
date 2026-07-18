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

    /// <summary>
    /// 生バイト範囲 <c>[fromKey, toKeyInclusive]</c> に対する forward-only seekable cursor。
    /// <see cref="Range"/> (leaf リンクの <c>ref struct</c>) と異なりヒープオブジェクトなので
    /// WAND がタームごとの cursor 配列を保持でき、<see cref="BTreeRawCursor.SeekTo"/> は
    /// tree root 経由の O(log N) ジャンプ (WAND pivoting の skip-pointer 代替) を行う。
    /// </summary>
    BTreeRawCursor OpenScanCursor(byte[] fromKey, byte[] toKeyInclusive);
    int Height { get; }
    long EntryCount { get; }
    IEnumerable<long> SeekValues(TKey key);
    IEnumerable<long> RangeValues(TKey from, bool fromInclusive, TKey to, bool toInclusive);
    IEnumerable<long> AllValues();
}

/// <summary>
/// 型パラメータに依存しない、索引の永続化フラッシュ用インタフェース。
/// <see cref="Checkpointer"/> が型を意識せずに <see cref="IIndexManager.FlushAll"/>
/// 経由で全索引を fsync するために使う。
/// orphan GC も型を意識せず全索引を走査するため、生キー / 生エントリ操作も
/// この non-generic 経路に乗せる。
/// </summary>
internal interface IBTreeIndexFlushable
{
    /// <summary>索引バッファプールのダーティページを fsync する。</summary>
    void Flush();

    /// <summary>
    /// 索引内の全 (生キー, 値) ペアを leaf 順に列挙する。
    /// scalar 索引値はプロパティ版参照を long で保持する。
    /// orphan 検出は呼び出し側 (IndexManager.ValidateAll) で行う。
    /// </summary>
    IEnumerable<KeyValuePair<byte[], long>> EnumerateRawEntries();

    /// <summary>
    /// 与えた (生キー, 値) ペアが索引に存在すれば削除する。
    /// orphan repair で <see cref="EnumerateRawEntries"/> から拾った生キーを
    /// そのまま使えるよう、コーデック経由のエンコードをスキップする経路。
    /// </summary>
    /// <returns>削除に成功したら <c>true</c>、ペアが見つからなければ <c>false</c>。</returns>
    bool DeleteRawEntry(ReadOnlySpan<byte> rawKey, long value);

    /// <summary>
    /// 生キーの idempotent set (recovery Pass 2b redo / Pass 3 undo 用)。
    /// 存在すれば値を上書き、無ければ挿入 (state-setting で二重適用が no-op)。WAL emit しない pure apply。
    /// </summary>
    void UpsertRaw(ReadOnlySpan<byte> rawKey, long value);

    /// <summary>
    /// abort / partial rollback の before-image undo がヘッダページを tx 開始前へ戻した後、
    /// B+Tree の in-memory キャッシュ (root / entryCount / height) をヘッダから読み直す。
    /// これを呼ばないと、rollback したページ変更に対して EntryCount が陳腐化し、
    /// 索引 split を含む tx の abort では root/height 不整合で破損し得る。
    /// </summary>
    void ReloadFromHeader();
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

internal readonly record struct ScalarIndexMetadata(
    ScalarIndexDefinition Definition,
    IndexLifecycleState State);

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
    /// 索引を <paramref name="oldName"/> から <paramref name="newName"/> へリネームする。
    /// 索引はテナント ID で識別されるため、テナント / B+Tree 実体・ページ・WAL 意味はすべて不変で、
    /// カタログ上の name キーと (label, propertyKey) バインディングだけが追従する。
    /// 旧名が見つからない場合は <c>false</c> を返す (冪等)。
    /// 既定実装は <see cref="NotSupportedException"/>。
    /// </summary>
    bool RenameIndex(string oldName, string newName)
        => throw new NotSupportedException("RenameIndex is not supported by this index manager.");

    void RenamePropertyTarget(string oldName, string newName) { }

    void RenameTargetScope(
        PropertyOwnerKind ownerKind,
        string oldName,
        string newName) { }

    /// <summary>
    /// スキーマ層から呼ばれ、(label, propertyKey) → indexName の対応を
    /// 登録する。これにより MergeVertex が業務キー検索で自動的にインデックスを利用できる。
    /// 既定実装は no-op (バインディングを保持しないバックエンドはフルスキャン経路に落ちる)。
    /// </summary>
    void RegisterIndexDefinition(
        ScalarIndexDefinition definition,
        IndexLifecycleState state = IndexLifecycleState.Ready) { }

    /// <summary>
    /// (label, propertyKey) に登録されたインデックス名を返す。
    /// バインディングが無い場合は <c>false</c> を返す (既定実装)。
    /// </summary>
    bool TryGetIndexName(string label, string propertyKey, out string indexName)
    {
        indexName = string.Empty;
        return false;
    }

    /// <summary>
    /// 登録済みインデックスのバインディングを列挙する
    /// (<see cref="ListIndexes"/> の補助。SchemaApi.ListIndexes のメタデータ復元に使う)。
    /// 既定実装は空シーケンス。
    /// </summary>
    IEnumerable<ScalarIndexMetadata> ListIndexDefinitions()
        => Array.Empty<ScalarIndexMetadata>();

    /// <summary>永続 definition の lifecycle state だけを更新する。</summary>
    void SetIndexState(string name, IndexLifecycleState state) { }

    /// <summary>
    /// definition と tenant identity を維持したまま derived B+Tree artifact を空に戻す。
    /// </summary>
    void ResetIndexArtifact(string name)
        => throw new NotSupportedException("Index artifact reset is not supported.");

    /// <summary>
    /// 管理下の全索引ファイルのバッファプールダーティページを fsync する。
    /// <see cref="Quiver.Transactions.Checkpointer"/> がチェックポイント時にデータファイル
    /// と並べて呼ぶ。これにより WAL truncate 後も索引内容が durable に残り、復旧時の
    /// 冪等 redo の起点として有効な状態が保証される。既定実装は no-op。
    /// </summary>
    void FlushAll() { }

    /// <summary>
    /// 全 B+Tree 索引を走査し、<paramref name="isLive"/> が <c>false</c> を返した
    /// scalar lane ではプロパティ版参照、全文 lane では entity 参照を持つエントリを orphan として収集する。
    /// 戻り値の <c>EntryCount</c> は走査総数、<c>IndexCount</c> は走査対象の索引数。
    /// 既定実装は何もせず (0, 0) を返す。
    /// </summary>
    (int IndexCount, long EntryCount) CollectOrphans(
        Func<long, bool> isLiveScalarReference,
        Func<long, bool> isLiveEntity,
        ICollection<(string IndexName, byte[] RawKey, long Value)> output)
        => (0, 0);

    /// <summary>
    /// <see cref="CollectOrphans"/> で得た一覧を実際に索引から削除する。
    /// 削除に成功した件数を返す。索引が dispose 済み等で見つからないエントリはスキップ。
    /// 既定実装は no-op。
    /// </summary>
    int RemoveOrphans(IEnumerable<(string IndexName, byte[] RawKey, long Value)> orphans) => 0;

    // ---- 全文索引 (postings + norms テナント) ----

    /// <summary>
    /// 全文索引 (postings + norms の 2 テナント) を作成する。既存なら既存を返す。
    /// 既定実装は <see cref="NotSupportedException"/> (binary backend のみ対応)。
    /// </summary>
    FullTextIndex CreateFullTextIndex(string name, string label, string propertyKey, string tokenizerId)
        => throw new NotSupportedException("Full-text indexes are not supported by this index manager.");

    /// <summary>名前で全文索引を引く。既定実装は false。</summary>
    bool TryGetFullTextIndex(string name, out FullTextIndex index)
    {
        index = null!;
        return false;
    }

    /// <summary>
    /// (label, propertyKey) に bound された全文索引を引く (透過維持フックの探索用)。
    /// 既定実装は false。
    /// </summary>
    bool TryGetFullTextIndexByLabelKey(string label, string propertyKey, out FullTextIndex index)
    {
        index = null!;
        return false;
    }

    /// <summary>登録済み全文索引のメタを列挙する。既定実装は空。</summary>
    IEnumerable<(string Name, string Label, string PropertyKey, string TokenizerId)> ListFullTextIndexes()
        => Array.Empty<(string, string, string, string)>();

    /// <summary>全文索引を削除する。既定実装は false。</summary>
    bool DropFullTextIndex(string name) => false;

    /// <summary>
    /// abort の before-image undo 後に、全 B+Tree 索引 (secondary + 全文) の in-memory
    /// ヘッダキャッシュを読み直す。<c>ReloadStoreMeta</c> から呼ばれる。既定は no-op。
    /// </summary>
    void ReloadAll() { }

    /// <summary>tokenizerId からトークナイザを解決する (catalog 記録値を registry 経由で)。</summary>
    ITokenizer ResolveTokenizer(string tokenizerId)
        => throw new NotSupportedException("This index manager has no tokenizer registry.");

    /// <summary>カスタムトークナイザ (フィルタ付きパイプライン等) を registry に登録する。</summary>
    void RegisterTokenizer(ITokenizer tokenizer) { }

    /// <summary>
    /// 全文索引が 1 つでも存在するか。透過維持フックの fast-path
    /// (FT 索引がゼロなら SetProperty はVertex読取を省略して素通り)。既定は false。
    /// </summary>
    bool HasAnyFullTextIndex => false;

    /// <summary>
    /// 透過維持: <paramref name="oldText"/> (before-image) の postings/norms を削除し、
    /// <paramref name="newText"/> を tokenize して挿入する。いずれも null ならその側はスキップ。
    /// 同一 Tx 内で呼ばれ、B+Tree 操作は page-WAL で保護される。既定は no-op。
    /// </summary>
    void MaintainFullText(FullTextIndex index, long entityId, string? oldText, string? newText) { }

}

internal interface IBulkLoadable<TKey>
{
    void BulkLoad(IEnumerable<KeyValuePair<TKey, long>> sortedEntries);
}
