using Quiver.Core;

namespace Quiver.Embedding;

/// <summary>
/// Idempotency log keyed by <see cref="EmbeddingTaskKey"/>. The pipeline
/// consults <see cref="GetInfoAsync"/> before invoking the provider so that
/// a successful run with the same content hash is a no-op.
/// </summary>
public interface IEmbeddingTaskLog
{
    ValueTask<EmbeddingTaskInfo> GetInfoAsync(EmbeddingTaskKey key, CancellationToken ct);

    ValueTask MarkInProgressAsync(EmbeddingTaskKey key, string contentHash, CancellationToken ct);

    ValueTask MarkCompletedAsync(EmbeddingTaskKey key, string contentHash, CancellationToken ct);

    ValueTask MarkFailedAsync(EmbeddingTaskKey key, string error, bool retryable, CancellationToken ct);
}

public readonly record struct EmbeddingTaskInfo(
    EmbeddingTaskState State,
    string? LastContentHash,
    DateTimeOffset? LastUpdatedAt);
