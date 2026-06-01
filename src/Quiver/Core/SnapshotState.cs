namespace Quiver.Core;

/// <summary>
/// FT-26: トランザクション開始時にキャプチャされるスナップショット。MVCC visibility 判定の入力。
///
/// <para>
/// LSN ベースの semi-MVCC ではなく、TxId と「自身開始時にアクティブだった TxId 集合」の
/// 2 つを持つ点が Postgres と同じ。<see cref="SnapshotTxId"/> 以下のコミット済み tx
/// (= committed AND txId &lt;= SnapshotTxId AND NOT ActiveAtBegin.Contains) が可視。
/// </para>
/// </summary>
internal readonly struct SnapshotState
{
    /// <summary>スナップショット取得時の高水位 TxId (自身の TxId はまだ含まれていない最大値)。</summary>
    public TransactionId SnapshotTxId { get; }

    /// <summary>
    /// スナップショット取得時にアクティブだった (= まだコミット/アボートしていない) tx 集合。
    /// これらの tx の writes はたとえ自身 commit 前であっても自身からは見えない。
    /// </summary>
    public IReadOnlySet<long> ActiveAtBegin { get; }

    public SnapshotState(TransactionId snapshotTxId, IReadOnlySet<long> activeAtBegin)
    {
        SnapshotTxId = snapshotTxId;
        ActiveAtBegin = activeAtBegin;
    }

    /// <summary>テスト / 起動経路用: 何も active の無いスナップショット (全 committed tx が見える)。</summary>
    public static SnapshotState Empty { get; } = new(new TransactionId(long.MaxValue), new HashSet<long>());
}
