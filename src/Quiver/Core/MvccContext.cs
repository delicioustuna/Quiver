namespace Quiver.Core;

/// <summary>
/// SSN (Serializable) の read-set を物理読み取り点で収集するための sink。
/// <c>Quiver.Transactions.SsnContext</c> が実装し、Serializable tx の間だけ
/// <see cref="MvccContext"/> に登録される。下層ストアの <c>Read</c> / <c>Scan</c> /
/// 隣接走査が「可視レコードを 1 件観測した」タイミングで <see cref="OnVisibleRead"/> を呼ぶ。
/// <para>これにより直接 Read だけでなく traversal / scan / index seek 経由の読み取りも
/// もれなく read-set に入り、SSN が rw-antidependency を取りこぼさない。phantom (述語に新規一致する
/// 行や隣接の増加) は別途 index versioning が必要なため対象外。</para>
/// </summary>
internal interface ISsnReadSink
{
    /// <summary>可視なバージョンを 1 件読み取ったことを記録する。</summary>
    void OnVisibleRead(EntityKind kind, long localId);
}

/// <summary>
/// MVCC アンビエントコンテキスト。<c>Quiver.Storage.Wal.WalPageContext</c> と対で
/// 非同期フロー単位にトランザクションの可視性スナップショット (TxId / ActiveAtBegin / committed registry)
/// を持つ。下層ストア (NodeStore / RelationshipStore / PropertyStore) はこれを参照して
/// record の xmin / xmax を埋め、可視性判定を行う。
/// <para>
/// コンテキスト未設定時は <see cref="TransactionId.Bootstrap"/> として扱う。これにより
/// ベンチマーク / bulk loader / recovery など tx 外から呼ばれる経路でも一貫した動作になる。
/// </para>
/// <para>
/// 配置: <c>Quiver.Storage.Records</c> から参照される必要があるため <c>Quiver.Core</c> に置く
/// (Quiver.Transactions は Stores より上位なので循環参照を避ける目的)。
/// </para>
/// </summary>
internal static class MvccContext
{
    private static readonly AsyncLocal<MvccTransactionContext?> CurrentSlot = new();

    private static MvccTransactionContext? Current
    {
        get => CurrentSlot.Value;
        set => CurrentSlot.Value = value;
    }

    /// <summary>
    /// 現在の非同期フローで MVCC トランザクションコンテキストを開始する。
    /// <paramref name="readSink"/> は SSN の read-set 収集先 (Serializable 時のみ非 null)。
    /// </summary>
    public static void Begin(TransactionId selfTxId, in SnapshotState snapshot, CommittedTxRegistry committed,
        ISsnReadSink? readSink = null)
    {
        MvccTransactionContext? current = Current;
        if (current.HasValue && current.Value.Matches(selfTxId, snapshot, committed, readSink))
            return;

        Current = new MvccTransactionContext(selfTxId, snapshot, committed, readSink);
    }

    /// <summary>現在の非同期フローのコンテキストを破棄する。</summary>
    public static void End() => Current = null;

    /// <summary>
    /// 下層ストアが可視レコードを 1 件読み取ったときに呼ぶ。SSN read-sink が
    /// 登録されていなければ (= Serializable 以外) 何もしない (ほぼゼロコスト)。
    /// </summary>
    public static void RecordRead(EntityKind kind, long localId)
        => Current?.ReadSink?.OnVisibleRead(kind, localId);

    /// <summary>現コンテキストの自身 TxId。未設定なら <see cref="TransactionId.Bootstrap"/>。</summary>
    public static TransactionId CurrentTxId => Current?.SelfTxId ?? TransactionId.Bootstrap;

    /// <summary>現コンテキストの snapshot。未設定なら <see cref="SnapshotState.Empty"/>。</summary>
    public static SnapshotState CurrentSnapshot => Current?.Snapshot ?? SnapshotState.Empty;

    /// <summary>現コンテキストの committed registry。未設定なら null。</summary>
    public static CommittedTxRegistry? CurrentCommitted => Current?.Committed;

    /// <summary>テスト用: コンテキストが設定されているか。</summary>
    public static bool IsActive => Current != null;
}

internal readonly struct MvccTransactionContext
{
    public TransactionId SelfTxId { get; }
    public SnapshotState Snapshot { get; }
    public CommittedTxRegistry Committed { get; }
    public ISsnReadSink? ReadSink { get; }

    public MvccTransactionContext(TransactionId selfTxId, in SnapshotState snapshot, CommittedTxRegistry committed,
        ISsnReadSink? readSink = null)
    {
        SelfTxId = selfTxId;
        Snapshot = snapshot;
        Committed = committed;
        ReadSink = readSink;
    }

    public bool Matches(
        TransactionId selfTxId,
        in SnapshotState snapshot,
        CommittedTxRegistry committed,
        ISsnReadSink? readSink)
        => SelfTxId == selfTxId
            && Snapshot.SnapshotTxId == snapshot.SnapshotTxId
            && ReferenceEquals(Snapshot.ActiveAtBegin, snapshot.ActiveAtBegin)
            && ReferenceEquals(Committed, committed)
            && ReferenceEquals(ReadSink, readSink);
}
