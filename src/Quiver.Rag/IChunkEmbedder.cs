namespace Quiver.Rag;

/// <summary>
/// チャンクテキストを埋め込みベクトルへ変換する呼び出し側注入点。
/// 埋め込み生成はエンジン外原則のため、Quiver.Rag はこの契約を呼ぶだけで実体を持たない。
/// 利用する埋め込みモデルに対して本契約を実装する。
/// </summary>
public interface IChunkEmbedder
{
    /// <summary>
    /// 埋め込みモデル、版、量子化、task 設定を一意に表す安定した識別子。
    /// <see cref="RagIngestionProfile.EmbeddingProfileId"/> と一致している必要がある。
    /// </summary>
    string ProfileId { get; }

    /// <summary>
    /// 生成するベクトルの次元数。
    /// <see cref="RagStoreOptions.EmbeddingDimensions"/> と一致している必要がある (ベクトル索引は作成時に次元が固定されるため)。
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// 複数チャンクのテキストをまとめて埋め込みへ変換する。
    /// 返却配列は <paramref name="texts"/> と同じ件数・同じ順序で、
    /// 各ベクトルは <see cref="Dimensions"/> 次元であること。
    /// トランザクション外で先に呼ばれ、失敗時は DB を変更しない。
    /// </summary>
    /// <param name="texts">埋め込み対象のチャンクテキスト列。</param>
    /// <param name="ct">キャンセルトークン。</param>
    ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
