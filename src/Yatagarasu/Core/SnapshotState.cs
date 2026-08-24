namespace Yatagarasu.Core;

/// <summary>
/// 読み取り開始時に固定するコミット済み状態です。
/// </summary>
internal readonly struct SnapshotState
{
    private static readonly IReadOnlySet<long> NoGaps = new HashSet<long>();

    internal SnapshotState(
        long committedHighWater,
        IReadOnlySet<long> abortedGaps,
        TransactionId? activeWriterId = null)
    {
        CommittedHighWater = committedHighWater;
        AbortedGaps = abortedGaps;
        ActiveWriterId = activeWriterId;
    }

    /// <summary>スナップショット取得時に確定していた最大トランザクションID。</summary>
    internal long CommittedHighWater { get; }

    /// <summary>高水位以下でコミットしなかった書き込みトランザクションID。</summary>
    internal IReadOnlySet<long> AbortedGaps { get; }

    /// <summary>取得時に存在した未コミットwriter。</summary>
    internal TransactionId? ActiveWriterId { get; }

    /// <summary>bootstrapデータだけを可視にする初期スナップショット。</summary>
    internal static SnapshotState Empty { get; } = new(
        TransactionId.Bootstrap.Value,
        NoGaps);
}
