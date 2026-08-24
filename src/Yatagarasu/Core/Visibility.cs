namespace Yatagarasu.Core;

/// <summary>
/// Single Writerスナップショットに対するMVCC可視性を判定します。
/// </summary>
internal static class Visibility
{
    internal static bool IsVisible(
        long xmin,
        long xmax,
        in SnapshotState snapshot,
        TransactionId self)
    {
        if (!IsCommittedAtSnapshot(xmin, in snapshot, self)) return false;
        return !IsCommittedDeleteAtSnapshot(xmax, in snapshot, self);
    }

    private static bool IsCommittedAtSnapshot(
        long txId,
        in SnapshotState snapshot,
        TransactionId self)
    {
        if (txId == 0) return false;
        if (txId == self.Value) return true;
        if (txId > snapshot.CommittedHighWater) return false;
        if (snapshot.ActiveWriterId?.Value == txId) return false;
        return !snapshot.AbortedGaps.Contains(txId);
    }

    private static bool IsCommittedDeleteAtSnapshot(
        long txId,
        in SnapshotState snapshot,
        TransactionId self)
    {
        if (txId == 0) return false;
        if (txId == self.Value) return true;
        if (txId > snapshot.CommittedHighWater) return false;
        if (snapshot.ActiveWriterId?.Value == txId) return false;

        // 中止済みxmaxを削除扱いにすると、失敗したwriterが既存versionを
        // readerから永久に隠すため、gapは必ず未コミットとして扱う。
        return !snapshot.AbortedGaps.Contains(txId);
    }
}
