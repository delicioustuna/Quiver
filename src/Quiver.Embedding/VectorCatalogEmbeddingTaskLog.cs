using Quiver.Core;

namespace Quiver.Embedding;

/// <summary>
/// Bridges <see cref="IEmbeddingTaskLog"/> onto the
/// <see cref="IVectorCatalog"/> so a single durable store backs both index
/// metadata and per-entity task state. The catalog implementation is
/// authoritative for concurrency / persistence — this wrapper is a thin
/// translation layer that lifts catalog records into the helper-facing
/// <see cref="EmbeddingTaskInfo"/> shape.
/// </summary>
public sealed class VectorCatalogEmbeddingTaskLog(IVectorCatalog catalog) : IEmbeddingTaskLog
{
    private readonly IVectorCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public ValueTask<EmbeddingTaskInfo> GetInfoAsync(EmbeddingTaskKey key, CancellationToken ct)
    {
        var rec = _catalog.GetTask(key);
        if (rec is null)
            return new ValueTask<EmbeddingTaskInfo>(
                new EmbeddingTaskInfo(EmbeddingTaskState.NotStarted, null, null));
        return new ValueTask<EmbeddingTaskInfo>(
            new EmbeddingTaskInfo(rec.State, rec.ContentHash, rec.LastUpdatedAtUtc));
    }

    public ValueTask MarkInProgressAsync(EmbeddingTaskKey key, string contentHash, CancellationToken ct)
    {
        _catalog.UpsertTask(new EmbeddingTaskRecord(
            key.EntityKind, key.EntityId, key.IndexName, key.ProviderId,
            EmbeddingTaskState.InProgress, contentHash, null, DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkCompletedAsync(EmbeddingTaskKey key, string contentHash, CancellationToken ct)
    {
        _catalog.UpsertTask(new EmbeddingTaskRecord(
            key.EntityKind, key.EntityId, key.IndexName, key.ProviderId,
            EmbeddingTaskState.Completed, contentHash, null, DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(EmbeddingTaskKey key, string error, bool retryable, CancellationToken ct)
    {
        var state = retryable ? EmbeddingTaskState.FailedRetryable : EmbeddingTaskState.FailedPermanent;
        var prev = _catalog.GetTask(key);
        _catalog.UpsertTask(new EmbeddingTaskRecord(
            key.EntityKind, key.EntityId, key.IndexName, key.ProviderId,
            state, prev?.ContentHash, error, DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }
}
