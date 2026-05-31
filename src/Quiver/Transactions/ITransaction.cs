using Quiver.Core;
using Quiver.Index;
using Quiver.Stores;

namespace Quiver.Transactions;

/// <summary>トランザクションコンテキスト。読み取りスナップショットと書き込みバッファを保持。</summary>
public interface ITransaction : IDisposable, ICommitHookRegistrar
{
    TransactionId Id { get; }
    IsolationLevel Level { get; }
    long SnapshotLsn { get; }
    TransactionState State { get; }

    /// <summary>コミット。WAL のフラッシュ完了まで同期的に待つ。</summary>
    void Commit();

    /// <summary>ロールバック。書き込み済み変更は破棄。</summary>
    void Abort();

    /// <summary>
    /// FT-23: トランザクション内に savepoint を作成し、その識別子を返す。
    /// 以後の変更を <see cref="RollbackTo"/> で巻き戻したり、<see cref="ReleaseSavepoint"/> で
    /// 親スコープへマージしたりできる。Nested savepoint をサポート。
    /// 非アクティブな tx では <see cref="TransactionException"/> をスロー。
    /// 部分ロールバックは durable ではない (クラッシュ復旧では tx 全体の abort/commit のみ反映される)。
    /// </summary>
    /// <param name="name">診断用の任意名。一意性は要求しない。</param>
    SavepointId Savepoint(string? name = null);

    /// <summary>
    /// FT-23: 指定 savepoint 以降の変更を巻き戻す。Savepoint 自体は消費されず、再度
    /// <see cref="RollbackTo"/> を呼ぶことができる (SQL 標準準拠)。
    /// 解放済みの savepoint や別 tx の savepoint を渡すと <see cref="TransactionException"/>。
    /// </summary>
    void RollbackTo(SavepointId savepoint);

    /// <summary>
    /// FT-23: 指定 savepoint を解放する (= 親スコープへマージし以後は無効化)。
    /// 親スコープの巻き戻し対象には savepoint 期間中に触れた未巻き戻しページが含まれる。
    /// </summary>
    void ReleaseSavepoint(SavepointId savepoint);

    INodeStore Nodes { get; }
    IRelationshipStore Relationships { get; }
    IPropertyStore Properties { get; }
    IIndexManager Indexes { get; }

    /// <summary>
    /// Contiguous adjacency index built by BulkLoader. Null when not yet built or after mutations.
    /// ExpandOperator uses this for faster neighbor scans; falls back to linked-list when null.
    /// </summary>
    IAdjacencyBlockStore? AdjacencyBlocks { get; }

    /// <summary>
    /// Backend-supplied access methods (BA-3). Operators call into this rather
    /// than reading <see cref="Nodes"/> / <see cref="Relationships"/> /
    /// <see cref="AdjacencyBlocks"/> directly. Defaults to
    /// <see cref="InlineGraphAccessMethods.Instance"/> when no backend-specific
    /// implementation was supplied at transaction construction time.
    /// </summary>
    IGraphAccessMethods Access { get; }
}

public enum TransactionState : byte
{
    Active = 1,
    Preparing = 2,
    Committed = 3,
    Aborted = 4,
}
