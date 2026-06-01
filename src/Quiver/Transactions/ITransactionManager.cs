using System.Diagnostics.CodeAnalysis;

namespace Quiver.Transactions;

/// <summary>トランザクション管理の入口。</summary>
internal interface ITransactionManager : IDisposable
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
    /// <remarks>
    /// DOC-2: SSN ベースの Serializable 分離は評価中のため <c>[Experimental("QUIVER001")]</c> 指定。
    /// SemVer の安定性保証対象外であり、シグネチャ・挙動は MINOR/PATCH でも変わりうる
    /// (docs/api-stability.md §5)。利用には診断 ID <c>QUIVER001</c> の suppress が必要。
    /// </remarks>
    [Experimental("QUIVER001")]
    Serializable = 3,
}
