using Quiver.Text;

namespace Quiver;

/// <summary>
/// <see cref="ISchemaApi.CreateFullTextIndex"/> に渡す全文索引オプション。
/// </summary>
/// <remarks>
/// トークナイザ ID はカタログに永続化され、検索時のトークン化がインデックス構築時と
/// 一致することを保証する。BM25 パラメータは検索オペレータが参照する。
/// </remarks>
public sealed record FullTextIndexOptions
{
    /// <summary>トークナイザ ID。トークナイザレジストリで解決される。既定: <c>mixed-bigram-v1</c>。</summary>
    public string TokenizerId { get; init; } = MixedBigramTokenizer.DefaultTokenizerId;

    /// <summary>
    /// トークナイザの後段に順序付きで適用するトークンフィルタ。
    /// </summary>
    /// <remarks>
    /// 非空のとき、実効トークナイザ ID は <c>"{TokenizerId}+{filter1}+{filter2}+…"</c> となり
    /// 自動登録される。検索時に同一パイプラインが再構築されることが保証される。
    /// </remarks>
    public IReadOnlyList<ITokenFilter> Filters { get; init; } = [];

    /// <summary>BM25 の term-frequency 飽和パラメータ k1 (既定 1.2)。</summary>
    public double K1 { get; init; } = 1.2;

    /// <summary>BM25 の文書長正規化パラメータ b (既定 0.75)。</summary>
    public double B { get; init; } = 0.75;
}

/// <summary>
/// <see cref="ISchemaApi.ListFullTextIndexes"/> が返す登録済み全文索引のメタ情報。
/// </summary>
public sealed record FullTextIndexInfo(
    string Name,
    string Label,
    string PropertyKey,
    string TokenizerId);
