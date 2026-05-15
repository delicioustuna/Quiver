using Quiver.Core;

namespace Quiver.Embedding.Providers;

/// <summary>
/// Adapter to whatever computes a float vector from text. Provider state
/// (HTTP client, local model handle) is held by the implementation; the
/// helper pipeline only sees these calls. See 10_embedding_pipeline.md §3.1.
/// </summary>
public interface IEmbeddingProvider : IAsyncDisposable
{
    /// <summary>Stable id used inside <see cref="EmbeddingTaskKey"/> so jobs survive provider swaps.</summary>
    string ProviderId { get; }

    int Dimensions { get; }

    DistanceMetric NativeMetric { get; }

    /// <summary>Upper bound on the number of in-flight <see cref="EmbedAsync"/> calls. Local-LLM providers usually report 1.</summary>
    int MaxConcurrency { get; }

    EmbeddingInputLimits Limits { get; }

    EmbeddingProviderCapabilities Capabilities { get; }

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
