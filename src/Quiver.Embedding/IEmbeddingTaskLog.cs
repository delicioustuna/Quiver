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

/// <summary>埋め込み処理の永続状態。</summary>
public enum EmbeddingTaskState : byte
{
    /// <summary>未着手。</summary>
    NotStarted,
    /// <summary>処理中。</summary>
    InProgress,
    /// <summary>完了。</summary>
    Completed,
    /// <summary>再試行可能な失敗。</summary>
    FailedRetryable,
    /// <summary>恒久的な失敗。</summary>
    FailedPermanent,
    /// <summary>元データ更新により陳腐化した状態。</summary>
    Stale,
}

/// <summary>
/// owner、vector target property、provider、normalization profileを区別するtask identity。
/// </summary>
/// <param name="Owner">埋め込みを書き込むowner。</param>
/// <param name="TargetPropertyName">vector property名。</param>
/// <param name="ProviderId">provider識別子。</param>
/// <param name="NormalizationProfile">正規化プロファイル名。</param>
public readonly record struct EmbeddingTaskKey(
    EntityRef Owner,
    string TargetPropertyName,
    string ProviderId,
    string NormalizationProfile);

/// <summary>埋め込みtaskの現在状態。</summary>
/// <param name="State">処理状態。</param>
/// <param name="LastContentHash">最後に処理したsource content hash。</param>
/// <param name="LastUpdatedAt">最終更新日時。</param>
public readonly record struct EmbeddingTaskInfo(
    EmbeddingTaskState State,
    string? LastContentHash,
    DateTimeOffset? LastUpdatedAt);
