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

    /// <summary>
    /// FT-33: SSN (Serial Safety Net, Wang et al. DaMoN'15) を用いた真の直列化可能分離レベル。
    /// snapshot isolation の上に commit 時の exclusion-window 検証 (π(T) &gt; η(T)) を重ね、
    /// write skew / read-only anomaly 等の SI アノマリを検出して一方を
    /// <see cref="SerializabilityException"/> で abort する。binary backend でのみ有効。
    /// </summary>
    Serializable = 3,
}
