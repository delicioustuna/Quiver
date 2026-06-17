namespace Quiver.Rag;

/// <summary>
/// チャンクテキストを埋め込みベクトルへ変換する呼び出し側注入点。埋め込み生成はエンジン外原則
/// のため、Quiver.Rag はこの契約を呼ぶだけで実体を持たない。通常は使う埋め込みモデル
/// (OpenAI API / ローカル ONNX 等) に対して本契約を直接実装する (<c>Quiver.Embedding</c> への依存は不要)。
/// <c>Quiver.Embedding</c> の <c>IEmbeddingProvider</c> を流用する場合は texts をループして
/// <c>EmbedAsync(EmbeddingRequest(text, Document))</c> の結果を集める薄いアダプタを利用アプリ側で合成する。
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
    /// 失敗時は DB を変更しない。
    /// </summary>
    /// <param name="texts">埋め込み対象のチャンクテキスト列。</param>
    /// <param name="ct">キャンセルトークン。</param>
    ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
