using Yatagarasu.Core;
using Yatagarasu.Text;

namespace Yatagarasu.Rag;

/// <summary>
/// <see cref="RagStore"/> の構成。取込 profile、ベクトル索引、全文索引、chunking を決める。
/// 索引は <see cref="RagStore"/> コンストラクタで冪等に作成される。
/// </summary>
public sealed record RagStoreOptions
{
    /// <summary>
    /// コーパス全体で固定する取込 profile。既存コーパスと一致しなければ
    /// <see cref="RagIngestionProfileMismatchException"/> で拒否する。
    /// </summary>
    public required RagIngestionProfile IngestionProfile { get; init; }

    /// <summary>
    /// 埋め込みベクトルの次元数。ベクトル索引作成時に固定されるため、
    /// 後で注入する <see cref="IChunkEmbedder.Dimensions"/> と必ず一致させること。
    /// </summary>
    public required int EmbeddingDimensions { get; init; }

    /// <summary>ベクトル索引の距離尺度。既定はコサイン類似度。</summary>
    public DistanceMetric VectorMetric { get; init; } = DistanceMetric.Cosine;

    /// <summary>ベクトル索引名。既定は <see cref="RagSchema.ChunkVectorIndex"/>。</summary>
    public string VectorIndexName { get; init; } = RagSchema.ChunkVectorIndex;

    /// <summary>
    /// Chunk.text 全文索引 (<see cref="RagSchema.ChunkTextIndex"/>) を<b>作成する</b>か。既定 <c>true</c>。
    /// これは作成可否のみを制御する。既存の索引はこの値が <c>false</c> でも維持・利用され
    /// (<see cref="RagStore.FullTextEnabled"/> は <c>true</c> のまま)。
    /// </summary>
    public bool EnableFullTextIndex { get; init; } = true;

    /// <summary>
    /// Chunk 全文索引で tokenizer の後段へ適用する opt-in filter。
    /// 既定は空で、異字体や類義語を暗黙には展開しない。
    /// </summary>
    /// <remarks>
    /// 日本語の機械的な異字体を検索時にも展開する場合は
    /// <see cref="JapaneseOrthographicVariantFilter"/> を明示する。
    /// filter 構成は corpus profile と全文索引 definition に永続化され、既存 corpus と異なる構成は拒否する。
    /// </remarks>
    public IReadOnlyList<ITokenFilter> FullTextFilters { get; init; }
        = Array.Empty<ITokenFilter>();

    /// <summary>チャンキング設定。取込時に Blocks をこの設定でチャンク化する。</summary>
    public ChunkingOptions Chunking { get; init; } = new();

    /// <summary>
    /// metadataJson の Document scan を避けるため、独立 string property と scalar index へ昇格する
    /// metadata key。既定は空で、従来どおり Document scan を使う。
    /// </summary>
    /// <remarks>
    /// 取込済みコーパスへ定義を追加すると profile 不一致になる。新しいコーパスを構築して切り替えるか、
    /// profile 未記録の旧コーパスで内容を特定できる場合だけ明示採用する。
    /// </remarks>
    public IReadOnlyList<RagMetadataIndex> MetadataIndexes { get; init; }
        = Array.Empty<RagMetadataIndex>();

    /// <summary>
    /// profile marker を持たない旧 Yatagarasu.Rag コーパスへ、指定した
    /// <see cref="IngestionProfile"/> が使われていたと呼び出し側が明示的に表明する。
    /// 既定は <c>false</c> で、旧コーパスは安全側に拒否する。
    /// </summary>
    /// <remarks>
    /// この設定は既存ベクトルを再生成しない。モデルと前処理を確実に特定できる場合だけ使う。
    /// metadata property の backfill も行わないため <see cref="MetadataIndexes"/> との同時指定は拒否する。
    /// 一度 marker が記録された後は通常の一致検証が行われる。
    /// </remarks>
    public bool AdoptLegacyIngestionProfile { get; init; }
}
