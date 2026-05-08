using GraphDb.Engine.Core;
using GraphDb.Engine.Index;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine.Transactions;

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
}

public enum TransactionState : byte
{
    Active = 1,
    Preparing = 2,
    Committed = 3,
    Aborted = 4,
}
