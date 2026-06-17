using Quiver.Core;

namespace Quiver.Rag;

/// <summary>
/// <see cref="RagStore"/> の構成。ベクトル索引の次元 / 距離尺度 / 名前と、全文索引を作るかどうかを決める。
/// 索引は <see cref="RagStore"/> コンストラクタで冪等に作成される。
/// </summary>
public sealed record RagStoreOptions
{
    /// <summary>
    /// 埋め込みベクトルの次元数。ベクトル索引作成時に固定されるため、後で注入する
    /// <see cref="IChunkEmbedder.Dimensions"/> と必ず一致させること。
    /// </summary>
    public required int EmbeddingDimensions { get; init; }

    /// <summary>ベクトル索引の距離尺度。既定はコサイン類似度。</summary>
    public DistanceMetric VectorMetric { get; init; } = DistanceMetric.Cosine;

    /// <summary>ベクトル索引に記録する埋め込みプロバイダ識別子。</summary>
    public string VectorProviderId { get; init; } = "external";

    /// <summary>ベクトル索引名。既定は <see cref="RagSchema.ChunkVectorIndex"/>。</summary>
    public string VectorIndexName { get; init; } = RagSchema.ChunkVectorIndex;

    /// <summary>
    /// Chunk.text 全文索引 (<see cref="RagSchema.ChunkTextIndex"/>) を<b>作成する</b>か。既定 <c>true</c>。
    /// これは作成可否のみを制御する。既存の索引はこの値が <c>false</c> でも維持・利用され
    /// (<see cref="RagStore.FullTextEnabled"/> は <c>true</c> のまま)。
    /// </summary>
    public bool EnableFullTextIndex { get; init; } = true;

    /// <summary>チャンキング設定。取込時に Blocks をこの設定でチャンク化する。</summary>
    public ChunkingOptions Chunking { get; init; } = new();
}
