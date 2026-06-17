namespace Quiver.Core;

/// <summary>
/// MVCC レコードの可視性判定。
///
/// <para>判定ロジック (Postgres SI に準拠):</para>
/// <code>
/// xmin の可視性:
///   xmin == 0                       → 不可視 (空きスロット / 未初期化)
///   xmin == self.TxId               → 可視 (自分の write を read-your-writes)
///   xmin &gt; snapshot.SnapshotTxId    → 不可視 (snapshot 以後の write)
///   xmin ∈ snapshot.ActiveAtBegin   → 不可視 (snapshot 開始時に並行 active だった)
///   committed.IsCommitted(xmin)     → 可視
///   それ以外                        → 不可視 (aborted / 未コミット)
///
/// xmax (xmin 可視を満たした上で評価):
///   xmax == 0                       → 可視 (削除されていない)
///   xmax == self.TxId               → 不可視 (自分が削除)
///   xmax &gt; snapshot.SnapshotTxId    → 可視 (snapshot 以後の削除は無視)
///   xmax ∈ snapshot.ActiveAtBegin   → 可視 (削除はまだコミットされていない)
///   committed.IsCommitted(xmax)     → 不可視 (snapshot 以前にコミット済み削除)
///   それ以外                        → 可視 (aborted / 未コミットの削除は無視)
/// </code>
/// </summary>
internal static class Visibility
{
    /// <summary>レコードの xmin / xmax から可視性を判定する。</summary>
    public static bool IsVisible(long xmin, long xmax, in SnapshotState snapshot, TransactionId self, CommittedTxRegistry committed)
    {
        if (!IsXminVisible(xmin, in snapshot, self, committed)) return false;
        return IsXmaxNotKilling(xmax, in snapshot, self, committed);
    }

    /// <summary>
    /// <c>MvccContext</c> 経由の便利 overload。コンテキスト未設定時は committed registry が
    /// null なので全 tx を「コミット済み扱い」とする (= bulk load / recovery 経路の振る舞い)。
    /// </summary>
    public static bool IsVisibleAmbient(long xmin, long xmax)
    {
        var committed = MvccContext.CurrentCommitted;
        if (committed == null)
        {
            // コンテキスト未設定: xmin が 0 でないかぎり全て可視 (= xmax が立っていない record のみ)
            if (xmin == 0) return false;
            return xmax == 0;
        }
        return IsVisible(xmin, xmax, MvccContext.CurrentSnapshot, MvccContext.CurrentTxId, committed);
    }

    private static bool IsXminVisible(long xmin, in SnapshotState snapshot, TransactionId self, CommittedTxRegistry committed)
    {
        if (xmin == 0) return false;
        if (xmin == self.Value) return true;
        if (xmin > snapshot.SnapshotTxId.Value) return false;
        if (snapshot.ActiveAtBegin.Contains(xmin)) return false;
        return committed.IsCommitted(xmin);
    }

    private static bool IsXmaxNotKilling(long xmax, in SnapshotState snapshot, TransactionId self, CommittedTxRegistry committed)
    {
        if (xmax == 0) return true;
        if (xmax == self.Value) return false;
        if (xmax > snapshot.SnapshotTxId.Value) return true;
        if (snapshot.ActiveAtBegin.Contains(xmax)) return true;
        // xmax がコミット済み → 削除は確定し snapshot から見えない。
        // それ以外 (aborted / 不明) → 削除は効力なし、可視のまま。
        return !committed.IsCommitted(xmax);
    }
}
