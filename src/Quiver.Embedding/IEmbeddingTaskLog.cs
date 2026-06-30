using Quiver.Core;

namespace Quiver.Embedding;

/// <summary>
/// <see cref="EmbeddingTaskKey"/> をキーとする冪等性ログ。
/// パイプラインはプロバイダの呼び出し前に <see cref="GetInfoAsync"/> を参照し、
/// 同じコンテンツハッシュで完了済みなら何もしない。
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
