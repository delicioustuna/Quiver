namespace Quiver.Transactions;

/// <summary>トランザクション管理の入口。</summary>
public interface ITransactionManager : IDisposable
{
    /// <summary>新規トランザクションを開始。</summary>
    ITransaction Begin(IsolationLevel level = IsolationLevel.SnapshotIsolation);

    /// <summary>現在実行中のトランザクション数。</summary>
    int ActiveCount { get; }

    /// <summary>最古アクティブトランザクションの LSN(VACUUM 用)。</summary>
    long OldestActiveLsn { get; }
}

public enum IsolationLevel : byte
{
    ReadCommitted = 1,
    SnapshotIsolation = 2,
}
