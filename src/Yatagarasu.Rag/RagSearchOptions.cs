namespace Yatagarasu.Rag;

/// <summary><see cref="RagSearcher.Search"/> の動作を制御するオプション。</summary>
public sealed record RagSearchOptions
{
    /// <summary>取得する上位件数 (融合後)。既定値は 10。</summary>
    public int K { get; init; } = 10;

    /// <summary>
    /// ヒットチャンクから <c>NEXT_CHUNK</c> を前後に辿って連結する件数。既定 1
    /// (前後 1 チャンクずつ)。0 でヒットチャンク単体。
    /// </summary>
    public int NeighborExpansion { get; init; } = 1;

    /// <summary>親文書情報 (<see cref="RagHit.Document"/>) を付与するか。既定 <c>true</c>。</summary>
    public bool IncludeDocument { get; init; } = true;

    /// <summary>
    /// 文書メタデータの等値制約 (キー → 期待値)。 <c>null</c>/空でなければ、
    /// <b>検索前</b>に全エントリが一致する文書のチャンクだけを母集団に絞ってから BM25 / KNN を実行する (candidate-side push-down)。
    /// </summary>
    /// <remarks>
    /// <see cref="MetadataFilter"/> の後段フィルタと違い、検索の母集団そのものを制約するため
    /// <b>recall hole が起きない</b> (フィルタが大半を弾いても、条件を満たす文書が存在する限り上位
    /// <see cref="K"/> 件を返せる)。比較対象は <see cref="RagMetadata.Metadata"/> (metadataJson) のキーで、
    /// 全キーが AND 条件。<see cref="MetadataFilter"/> と併用した場合は push-down 後にさらに後段適用される。
    /// 対象 key に <see cref="RagStoreOptions.MetadataIndexes"/> の定義があれば scalar index seek、
    /// なければラベルスキャン 1 回 + チャンク列挙を使う。低選択率で頻用する string key は
    /// <see cref="RagMetadataIndex"/> へ明示昇格できる。
    /// </remarks>
    public IReadOnlyDictionary<string, string>? MetadataEquals { get; init; }

    /// <summary>
    /// 文書メタデータに対する述語。<c>null</c> でなければ、これが <c>false</c> を返した文書のヒットを結果から除外する。
    /// </summary>
    /// <remarks>
    /// これは<b>後段フィルタ</b>である (上位 <see cref="K"/> 件を取得してから除外する)。
    /// フィルタが大半を弾くワークロードでは、条件を満たす文書が存在しても結果が 0 件になり得る (recall hole)。
    /// 等値制約で十分なら <see cref="MetadataEquals"/> を使うと push-down され recall hole を避けられる。
    /// 範囲条件・複雑な述語など <see cref="MetadataEquals"/> で表せないものに本フィルタを使う。
    /// </remarks>
    public Func<RagMetadata, bool>? MetadataFilter { get; init; }
}
