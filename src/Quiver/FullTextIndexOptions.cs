using Quiver.Text;

namespace Quiver;

/// <summary>
/// Options for <see cref="ISchemaApi.CreateFullTextIndex"/>. The tokenizer id is
/// recorded in the catalog so query-time tokenization matches index-time
/// tokenization. BM25 parameters are carried here for the search operator (FTS-3).
/// </summary>
public sealed record FullTextIndexOptions
{
    /// <summary>Tokenizer id, resolved through the tokenizer registry. Default: <c>mixed-bigram-v1</c>.</summary>
    public string TokenizerId { get; init; } = MixedBigramTokenizer.DefaultTokenizerId;

    /// <summary>BM25 term-frequency saturation parameter k1 (default 1.2).</summary>
    public double K1 { get; init; } = 1.2;

    /// <summary>BM25 length-normalization parameter b (default 0.75).</summary>
    public double B { get; init; } = 0.75;
}

/// <summary>Metadata for a registered full-text index, returned by <see cref="ISchemaApi.ListFullTextIndexes"/>.</summary>
public sealed record FullTextIndexInfo(
    string Name,
    string Label,
    string PropertyKey,
    string TokenizerId);
