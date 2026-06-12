namespace Quiver.Rag;

/// <summary><see cref="RagSearcher.Search"/> の動作を制御するオプション。</summary>
public sealed record RagSearchOptions
{
    /// <summary>取得する上位件数 (融合後)。既定 10。</summary>
    public int K { get; init; } = 10;

    /// <summary>
    /// ヒットチャンクから <c>NEXT_CHUNK</c> を前後に辿って連結する件数。既定 1
    /// (前後 1 チャンクずつ)。0 でヒットチャンク単体。
    /// </summary>
    public int NeighborExpansion { get; init; } = 1;

    /// <summary>親文書情報 (<see cref="RagHit.Document"/>) を付与するか。既定 <c>true</c>。</summary>
    public bool IncludeDocument { get; init; } = true;

    /// <summary>
    /// 文書メタデータに対する述語。<c>null</c> でなければ、これが <c>false</c> を返した文書の
    /// ヒットを結果から除外する。
    /// </summary>
    /// <remarks>
    /// これは<b>後段フィルタ</b>である (上位 <see cref="K"/> 件を取得してから除外する)。フィルタが
    /// 大半を弾くワークロードでは、条件を満たす文書が存在しても結果が 0 件になり得る (recall hole)。
    /// 将来は candidate-side push-down (<c>FilterByText</c>/<c>KnnSearchFiltered</c>) へ寄せられる。
    /// </remarks>
    public Func<RagMetadata, bool>? MetadataFilter { get; init; }
}
