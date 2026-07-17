using System.Diagnostics.CodeAnalysis;

namespace Quiver.Transactions;

/// <summary>内部トランザクション管理の入口です。</summary>
internal interface ITransactionManager : IDisposable
{
    /// <summary>読み取り専用スナップショットを開始します。</summary>
    ITransaction BeginRead();

    /// <summary>書き込み所有権を取得してトランザクションを開始します。</summary>
    ITransaction BeginWrite(IsolationLevel level = IsolationLevel.SnapshotIsolation);

    /// <summary>現在実行中の読み取りと書き込みの合計数。</summary>
    int ActiveCount { get; }

    /// <summary>最古アクティブトランザクションのLSN。</summary>
    long OldestActiveLsn { get; }
}

/// <summary>公開facadeが保持する分離レベル指定です。</summary>
public enum IsolationLevel : byte
{
    /// <summary>移行期間中はスナップショット分離へ適応されます。</summary>
    ReadCommitted = 1,

    /// <summary>トランザクション開始時点の一貫したスナップショットを参照します。</summary>
    SnapshotIsolation = 2,

    /// <summary>移行期間中はスナップショット分離へ適応されます。</summary>
    [Experimental("QUIVER001")]
    Serializable = 3,
}
