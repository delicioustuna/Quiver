namespace Quiver.Core;

/// <summary>
/// FT-26: MVCC アンビエントコンテキスト。<c>Quiver.Wal.WalPageContext</c> と対で
/// スレッドローカルにトランザクションの可視性スナップショット (TxId / ActiveAtBegin / committed registry)
/// を持つ。下層ストア (NodeStore / RelationshipStore / PropertyStore) はこれを参照して
/// record の xmin / xmax を埋め、可視性判定を行う。
///
/// <para>
/// コンテキスト未設定時は <see cref="TransactionId.Bootstrap"/> として扱う。これにより
/// ベンチマーク / bulk loader / recovery など tx 外から呼ばれる経路でも一貫した動作になる。
/// </para>
///
/// <para>
/// 配置: <c>Quiver.Stores</c> から参照される必要があるため <c>Quiver.Core</c> に置く
/// (Quiver.Transactions は Stores より上位なので循環参照を避ける目的)。
/// </para>
/// </summary>
public static class MvccContext
{
    [ThreadStatic]
    private static MvccTransactionContext? _current;

    /// <summary>このスレッドで MVCC トランザクションコンテキストを開始する。</summary>
    public static void Begin(TransactionId selfTxId, in SnapshotState snapshot, CommittedTxRegistry committed)
        => _current = new MvccTransactionContext(selfTxId, snapshot, committed);

    /// <summary>このスレッドのコンテキストを破棄する。</summary>
    public static void End() => _current = null;

    /// <summary>現コンテキストの自身 TxId。未設定なら <see cref="TransactionId.Bootstrap"/>。</summary>
    public static TransactionId CurrentTxId => _current?.SelfTxId ?? TransactionId.Bootstrap;

    /// <summary>現コンテキストの snapshot。未設定なら <see cref="SnapshotState.Empty"/>。</summary>
    public static SnapshotState CurrentSnapshot => _current?.Snapshot ?? SnapshotState.Empty;

    /// <summary>現コンテキストの committed registry。未設定なら null。</summary>
    public static CommittedTxRegistry? CurrentCommitted => _current?.Committed;

    /// <summary>テスト用: コンテキストが設定されているか。</summary>
    public static bool IsActive => _current != null;
}

internal sealed class MvccTransactionContext
{
    public TransactionId SelfTxId { get; }
    public SnapshotState Snapshot { get; }
    public CommittedTxRegistry Committed { get; }

    public MvccTransactionContext(TransactionId selfTxId, in SnapshotState snapshot, CommittedTxRegistry committed)
    {
        SelfTxId = selfTxId;
        Snapshot = snapshot;
        Committed = committed;
    }
}
