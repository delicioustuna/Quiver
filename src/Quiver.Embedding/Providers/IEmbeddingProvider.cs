using Quiver.Core;

namespace Quiver.Embedding.Providers;

/// <summary>
/// テキストから float ベクトルを生成する処理の抽象。プロバイダ固有の状態
/// (HTTP クライアント、ローカルモデルハンドルなど) は実装側が保持し、
/// ヘルパパイプラインはこれらの呼び出しだけを見る。
/// </summary>
public interface IEmbeddingProvider : IAsyncDisposable
{
    /// <summary><see cref="EmbeddingTaskKey"/> 内で使う安定 ID。プロバイダ切り替え後もジョブが維持されるようにする。</summary>
    string ProviderId { get; }

    /// <summary>生成されるベクトルの次元数。</summary>
    int Dimensions { get; }

    /// <summary>プロバイダがネイティブに使う距離メトリック。</summary>
    DistanceMetric NativeMetric { get; }

    /// <summary>同時に走らせる <see cref="EmbedAsync"/> 呼び出し数の上限。ローカル LLM プロバイダは通常 1 を返す。</summary>
    int MaxConcurrency { get; }

    /// <summary>プロバイダが受け付ける入力長の制限。</summary>
    EmbeddingInputLimits Limits { get; }

    /// <summary>プロバイダのケイパビリティ。</summary>
    EmbeddingProviderCapabilities Capabilities { get; }

    /// <summary>テキストを 1 件の埋め込みベクトルに変換する。</summary>
    ValueTask<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct);
}

public sealed record EmbeddingRequest(
    string Text,
    EmbeddingPurpose Purpose = EmbeddingPurpose.Document,
    string? CorrelationId = null);

public enum EmbeddingPurpose : byte
{
    Document = 1,
    Query = 2,
}

public sealed record EmbeddingResult(
    ReadOnlyMemory<float> Vector,
    int InputLengthMeasured,
    LengthCountingMode MeasurementUnit,
    bool WasTruncated,
    TimeSpan Latency,
    string? CorrelationId);

public sealed record EmbeddingInputLimits(
    int MaxLength,
    LengthCountingMode CountingMode,
    TruncationPolicy DefaultTruncation);

public enum LengthCountingMode : byte
{
    Tokens = 1,
    Utf8Bytes = 2,
    Utf16CodeUnits = 3,
    Runes = 4,
    Graphemes = 5,
}

public enum TruncationPolicy : byte
{
    Tail = 1,
    Head = 2,
    MiddleEllipsis = 3,
    ThrowOnExceed = 4,
}

public sealed record EmbeddingProviderCapabilities
{
    public required EmojiTokenizationQuality EmojiQuality { get; init; }
    public required bool RecognizesZwjSequences { get; init; }
    public required bool RecognizesSkinToneModifiers { get; init; }
    public required bool RequiresQueryDocumentDistinction { get; init; }
    public required bool SupportsBatchInput { get; init; }
}

public enum EmojiTokenizationQuality : byte
{
    Unknown = 0,
    Poor = 1,
    Adequate = 2,
    Good = 3,
}
