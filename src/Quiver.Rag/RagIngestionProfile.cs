namespace Quiver.Rag;

/// <summary>チャンクから埋め込み入力を組み立てる既定テンプレート。</summary>
public enum RagEmbeddingInputTemplate
{
    /// <summary>チャンク本文だけを埋め込み入力にする。</summary>
    ChunkText,

    /// <summary>見出しパスがあれば改行を挟んでチャンク本文の前に置く。</summary>
    HeadingPathAndChunkText,
}

/// <summary>
/// 一つの RAG コーパスで混在させない取込 semantics の識別情報。
/// 実際の chunking 数値は <see cref="RagStoreOptions.Chunking"/> と併せて fingerprint に含まれる。
/// </summary>
public sealed record RagIngestionProfile
{
    /// <summary>
    /// チャンカー実装または呼び出し側規約の安定した識別子。
    /// 同じ数値設定でも分割 semantics が変わる場合は別の値を指定する。
    /// </summary>
    public string ChunkingProfileId { get; init; } = "quiver-chunker-v1";

    /// <summary>
    /// 埋め込みモデル、版、量子化、task 設定を一意に表す識別子。
    /// <see cref="IChunkEmbedder.ProfileId"/> と一致している必要がある。
    /// </summary>
    public required string EmbeddingProfileId { get; init; }

    /// <summary>
    /// <see cref="IngestedDocument.Blocks"/> を生成した正規化規約の識別子。
    /// OCR、Unicode、空白処理などの意味が変わる場合は別の値を指定する。
    /// </summary>
    public string NormalizationProfileId { get; init; } = "caller-normalized-blocks-v1";

    /// <summary>チャンクから埋め込み入力を組み立てるテンプレート。</summary>
    public RagEmbeddingInputTemplate EmbeddingInputTemplate { get; init; }
        = RagEmbeddingInputTemplate.HeadingPathAndChunkText;

    internal void Validate()
    {
        ValidateId(ChunkingProfileId, nameof(ChunkingProfileId));
        ValidateId(EmbeddingProfileId, nameof(EmbeddingProfileId));
        ValidateId(NormalizationProfileId, nameof(NormalizationProfileId));
        if (!Enum.IsDefined(EmbeddingInputTemplate))
            throw new ArgumentOutOfRangeException(
                nameof(EmbeddingInputTemplate),
                EmbeddingInputTemplate,
                "未対応の埋め込み入力テンプレートです。");
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("profile ID は空にできません。", parameterName);
    }
}

/// <summary>要求された取込 profile が既存コーパスの profile と一致しないことを表す。</summary>
public sealed class RagIngestionProfileMismatchException : InvalidOperationException
{
    /// <summary>既存コーパスに記録されている profile fingerprint。</summary>
    public string StoredFingerprint { get; }

    /// <summary>今回要求された profile fingerprint。</summary>
    public string RequestedFingerprint { get; }

    /// <summary>profile fingerprint と診断メッセージを指定して例外を作成する。</summary>
    /// <param name="storedFingerprint">既存コーパスの fingerprint。未記録なら空文字。</param>
    /// <param name="requestedFingerprint">今回要求された fingerprint。</param>
    /// <param name="message">不一致の説明。</param>
    public RagIngestionProfileMismatchException(
        string storedFingerprint,
        string requestedFingerprint,
        string message)
        : base(message)
    {
        StoredFingerprint = storedFingerprint;
        RequestedFingerprint = requestedFingerprint;
    }
}
