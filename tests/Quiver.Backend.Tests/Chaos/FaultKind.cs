namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// TS-4: chaos scenario が注入する障害の種別。<see cref="ChaosScenario"/> から
/// <see cref="IFaultInjector"/> 経由で適用される。
///
/// 全ての fault は「アクティブな書き込みが進行している途中で OS / プロセスが落ちる」
/// 状況を模擬する。各 fault に対する recovery の正当性契約は
/// <see cref="ChaosScenarioRunner"/> が consistency check で検証する。
/// </summary>
public enum FaultKind
{
    /// <summary>workload 実行中の何らかの時点で <see cref="Faults.KillProcessSimulator"/> を発火。</summary>
    KillOnly,

    /// <summary>kill 後、最終 WAL セグメント末尾 16 バイトを zero-fill (FT-15 の torn-write モデル)。</summary>
    KillThenTornWalTail,

    /// <summary>kill 後、最終 WAL セグメントの先頭レコードヘッダの 1 bit を flip (checksum mismatch)。</summary>
    KillThenChecksumFlip,

    /// <summary>kill 後、<c>adj.epoch</c> sidecar ファイルを削除 (rebuild fallback 経路の検証)。</summary>
    KillThenSidecarDelete,

    /// <summary>workload 末尾の tx を <c>Rollback</c> してから kill — uncommitted データが残らないこと。</summary>
    AbortThenKill,

    /// <summary>checkpoint phase <see cref="Transactions.CheckpointPhase.AfterBegin"/> 完了直後に kill。</summary>
    CheckpointKillAfterBegin,

    /// <summary>checkpoint phase <see cref="Transactions.CheckpointPhase.AfterDataFlush"/> 完了直後に kill。</summary>
    CheckpointKillAfterDataFlush,

    /// <summary>checkpoint phase <see cref="Transactions.CheckpointPhase.AfterIndexFlush"/> 完了直後に kill。</summary>
    CheckpointKillAfterIndexFlush,

    /// <summary>checkpoint phase <see cref="Transactions.CheckpointPhase.AfterEnd"/> 完了直後に kill。</summary>
    CheckpointKillAfterEnd,

    /// <summary>checkpoint phase <see cref="Transactions.CheckpointPhase.AfterTruncate"/> 完了直後に kill (clean checkpoint 完走 + kill)。</summary>
    CheckpointKillAfterTruncate,
}
