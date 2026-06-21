using Quiver.Core;
using Quiver.Index;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

/// <summary>トランザクションコンテキスト。読み取りスナップショットと書き込みバッファを保持。</summary>
internal interface ITransaction : IDisposable, ICommitHookRegistrar
{
    TransactionId Id { get; }
    IsolationLevel Level { get; }
    long SnapshotLsn { get; }
    TransactionState State { get; }

    /// <summary>
    /// この tx の MVCC 可視性スナップショット。列スキャン集約が
    /// operator 経路を介さず直接可視性判定するために露出する。
    /// </summary>
    SnapshotState Snapshot { get; }

    /// <summary>
    /// committed TxId レジストリ (可視性判定用)。旧テスト互換経路では null。
    /// </summary>
    CommittedTxRegistry? Committed { get; }

    /// <summary>コミット。WAL のフラッシュ完了まで同期的に待つ。</summary>
    void Commit();

    /// <summary>ロールバック。書き込み済み変更は破棄。</summary>
    void Abort();

    /// <summary>
    /// トランザクション内に savepoint を作成し、その識別子を返す。
    /// 以後の変更を <see cref="RollbackTo"/> で巻き戻したり、<see cref="ReleaseSavepoint"/> で
    /// 親スコープへマージしたりできる。Nested savepoint をサポート。
    /// 非アクティブな tx では <see cref="TransactionException"/> をスロー。
    /// 部分ロールバックは durable ではない (クラッシュ復旧では tx 全体の abort/commit のみ反映される)。
    /// </summary>
    /// <param name="name">診断用の任意名。一意性は要求しない。</param>
    SavepointId Savepoint(string? name = null);

    /// <summary>
    /// 指定 savepoint 以降の変更を巻き戻す。Savepoint 自体は消費されず、再度
    /// <see cref="RollbackTo"/> を呼ぶことができる (SQL 標準準拠)。
    /// 解放済みの savepoint や別 tx の savepoint を渡すと <see cref="TransactionException"/>。
    /// </summary>
    void RollbackTo(SavepointId savepoint);

    /// <summary>
    /// 指定 savepoint を解放する (= 親スコープへマージし以後は無効化)。
    /// 親スコープの巻き戻し対象には savepoint 期間中に触れた未巻き戻しページが含まれる。
    /// </summary>
    void ReleaseSavepoint(SavepointId savepoint);

    INodeStore Nodes { get; }
    IRelationshipStore Relationships { get; }
    IPropertyStore Properties { get; }
    IIndexManager Indexes { get; }

    // BulkLoader が構築する連続隣接インデックス。未構築またはミューテーション後は null。
    // ExpandOperator が高速な隣接スキャンに使い、null のときはリンクリストにフォールバック。
    IAdjacencyBlockStore? AdjacencyBlocks { get; }

    // バックエンド提供のアクセスメソッド。オペレータは Nodes/Relationships/AdjacencyBlocks を
    // 直接読まずこちら経由で呼ぶ。バックエンド固有実装が無い場合は InlineGraphAccessMethods.Instance。
    IGraphAccessMethods Access { get; }
}

/// <summary>トランザクションのライフサイクル状態。</summary>
public enum TransactionState : byte
{
    /// <summary>実行中 (読み書き可能)。</summary>
    Active = 1,
    /// <summary>コミット準備中 (prepare フェーズ)。</summary>
    Preparing = 2,
    /// <summary>コミット済み。</summary>
    Committed = 3,
    /// <summary>ロールバック済み。</summary>
    Aborted = 4,
}
