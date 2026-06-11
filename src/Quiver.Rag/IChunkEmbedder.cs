namespace Quiver.Rag;

/// <summary>
/// チャンクテキストを埋め込みベクトルへ変換する呼び出し側注入点。埋め込み生成はエンジン外原則
/// (14_rag_layer.md §4) のため、Quiver.Rag はこの契約を呼ぶだけで実体を持たない。
/// <c>Quiver.Embedding</c> の <c>TextEmbeddingPipeline</c> を繋ぐアダプタは利用アプリ側 or サンプルで合成する。
/// </summary>
public interface IChunkEmbedder
{
    /// <summary>
    /// 生成するベクトルの次元数。<see cref="RagStoreOptions.EmbeddingDimensions"/> と一致している必要がある
    /// (ベクトル索引は作成時に次元が固定されるため)。
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// 複数チャンクのテキストをまとめて埋め込みへ変換する。返却配列は <paramref name="texts"/> と同じ件数・
    /// 同じ順序で、各ベクトルは <see cref="Dimensions"/> 次元であること。トランザクション外で先に呼ばれ、
    /// 失敗時は DB を変更しない (RAG-3)。
    /// </summary>
    /// <param name="texts">埋め込み対象のチャンクテキスト列。</param>
    /// <param name="ct">キャンセルトークン。</param>
    ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
