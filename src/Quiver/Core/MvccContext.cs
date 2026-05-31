namespace Quiver.Core;

/// <summary>
/// FT-33: SSN (Serializable) の read-set を物理読み取り点で収集するための sink。
/// <c>Quiver.Transactions.SsnContext</c> が実装し、Serializable tx の間だけ
/// <see cref="MvccContext"/> に登録される。下層ストアの <c>Read</c> / <c>Scan</c> /
/// 隣接走査が「可視レコードを 1 件観測した」タイミングで <see cref="OnVisibleRead"/> を呼ぶ。
///
/// <para>これにより直接 Read だけでなく traversal / scan / index seek 経由の読み取りも
/// もれなく read-set に入り、SSN が rw-antidependency を取りこぼさない。phantom (述語に新規一致する
/// 行や隣接の増加) は別途 index versioning が必要なため対象外。</para>
/// </summary>
public interface ISsnReadSink
{
    /// <summary>可視なバージョンを 1 件読み取ったことを記録する。</summary>
    void OnVisibleRead(EntityKind kind, long localId);
}

/// <summary>
/// FT-26: MVCC アンビエントコンテキスト。<c>Quiver.Storage.Wal.WalPageContext</c> と対で
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
/// 配置: <c>Quiver.Storage.Records</c> から参照される必要があるため <c>Quiver.Core</c> に置く
/// (Quiver.Transactions は Stores より上位なので循環参照を避ける目的)。
/// </para>
/// </summary>
public static class MvccContext
{
    [ThreadStatic]
    private static MvccTransactionContext? _current;

    /// <summary>
    /// このスレッドで MVCC トランザクションコンテキストを開始する。
    /// <paramref name="readSink"/> は FT-33 SSN の read-set 収集先 (Serializable 時のみ非 null)。
    /// </summary>
    public static void Begin(TransactionId selfTxId, in SnapshotState snapshot, CommittedTxRegistry committed,
        ISsnReadSink? readSink = null)
        => _current = new MvccTransactionContext(selfTxId, snapshot, committed, readSink);

    /// <summary>このスレッドのコンテキストを破棄する。</summary>
    public static void End() => _current = null;

    /// <summary>
    /// FT-33: 下層ストアが可視レコードを 1 件読み取ったときに呼ぶ。SSN read-sink が
    /// 登録されていなければ (= Serializable 以外) 何もしない (ほぼゼロコスト)。
    /// </summary>
    public static void RecordRead(EntityKind kind, long localId)
        => _current?.ReadSink?.OnVisibleRead(kind, localId);

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
    public ISsnReadSink? ReadSink { get; }

    public MvccTransactionContext(TransactionId selfTxId, in SnapshotState snapshot, CommittedTxRegistry committed,
        ISsnReadSink? readSink = null)
    {
        SelfTxId = selfTxId;
        Snapshot = snapshot;
        Committed = committed;
        ReadSink = readSink;
    }
}
