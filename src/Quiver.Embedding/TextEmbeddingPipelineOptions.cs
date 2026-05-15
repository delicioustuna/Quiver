using Quiver.Core;
using Quiver.Embedding.Text;

namespace Quiver.Embedding;

public sealed class TextEmbeddingPipelineOptions
{
    public int QueueCapacity { get; init; } = 10_000;
    public BackpressureMode Backpressure { get; init; } = BackpressureMode.Wait;
    public EmojiPolicy EmojiPolicy { get; init; } = new();
}

public enum BackpressureMode : byte
{
    Wait = 0,
    DropOldest = 1,
    DropNewest = 2,
    ThrowOnFull = 3,
}

/// <summary>
/// Inputs to <c>ScanAndEnqueueAsync</c> (Z'): which entity kind to walk,
/// which property holds the source text, and which vector index to backfill
/// into. The pipeline derives the rest (content hash, idempotency check)
/// from the source text and the current task log state.
/// </summary>
public sealed record EmbeddingScanSpec
{
    public required string TargetIndexName { get; init; }
    public required string SourcePropertyName { get; init; }
    public required EntityKind Kind { get; init; }
}
