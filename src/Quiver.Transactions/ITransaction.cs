using Quiver.Core;
using Quiver.Index;
using Quiver.Stores;

namespace Quiver.Transactions;

/// <summary>トランザクションコンテキスト。読み取りスナップショットと書き込みバッファを保持。</summary>
public interface ITransaction : IDisposable
{
    TransactionId Id { get; }
    IsolationLevel Level { get; }
    long SnapshotLsn { get; }
    TransactionState State { get; }

    /// <summary>コミット。WAL のフラッシュ完了まで同期的に待つ。</summary>
    void Commit();

    /// <summary>ロールバック。書き込み済み変更は破棄。</summary>
    void Abort();

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
