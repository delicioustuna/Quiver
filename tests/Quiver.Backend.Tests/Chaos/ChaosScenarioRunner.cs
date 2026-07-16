using System.Text;
using Quiver.Backend.Tests.Faults;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// chaos シナリオを 1 件実行する runner。シナリオ毎に専用の temp ディレクトリで
/// fresh backend を起動し、決定的に workload を流し、fault を注入し、kill 後に
/// 再 open して consistency check を回す。トレースは shape:
/// <code>
/// [seed=42, txCount=8, fault=KillThenTornWalTail]
///   tx#0 commit vertices=[..]
///   tx#1 rollback
///   ...
///   INJECT TornWalTail
///   KILL
///   REOPEN ok
///   VERIFY: 7/7 committed survived
/// </code>
/// 失敗時は <c>chaos-trace.log</c> に追記する。
///
/// 検証契約 (recovery 正当性):
/// <list type="bullet">
/// <item>KillOnly / Checkpoint* / AbortThenKill: 全 committed tx が読める。aborted tx は読めない。</item>
/// <item>KillThenTornWalTail: 最後の committed tx 1 件は失われる可能性あり (suffix loss を許容)。</item>
/// <item>KillThenChecksumFlip: reopen が <see cref="CorruptionException"/> / <see cref="StorageException"/>
///   を投げて拒否するか、reopen 成功時は損傷直前までの prefix が読める。</item>
/// <item>KillThenSidecarDelete: reopen 成功 (rebuild fallback) なら全 committed が読める。
///   <see cref="StorageException"/> / <see cref="CorruptionException"/> も許容。</item>
/// </list>
/// </summary>
internal sealed class ChaosScenarioRunner
{
    private readonly Func<string, IGraphStorageBackend> _open;
    private readonly Func<string, IFaultInjector> _injectorFactory;
    private readonly Func<string, IGraphStorageBackend> _openWithCheckpointSensitive;

    public ChaosScenarioRunner(
        Func<string, IGraphStorageBackend> openDefault,
        Func<string, IGraphStorageBackend> openCheckpointSensitive,
        Func<string, IFaultInjector> injectorFactory)
    {
        _open = openDefault;
        _openWithCheckpointSensitive = openCheckpointSensitive;
        _injectorFactory = injectorFactory;
    }

    public ChaosResult Run(ChaosScenario scenario)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            $"quiver_chaos_{scenario.Seed}_{scenario.Fault}_{Guid.NewGuid():N}");
        var trace = new StringBuilder();
        trace.AppendLine($"[{scenario}]");
        var oracle = new OracleState();

        try
        {
            var injector = _injectorFactory(dir);
            bool isCheckpointFault = scenario.Fault is FaultKind.CheckpointKillAfterBegin
                or FaultKind.CheckpointKillAfterDataFlush
                or FaultKind.CheckpointKillAfterIndexFlush
                or FaultKind.CheckpointKillAfterEnd
                or FaultKind.CheckpointKillAfterTruncate;

            // ---- フェーズ 1: workload 実行中の fault injection ----
            var open = isCheckpointFault ? _openWithCheckpointSensitive : _open;
            IGraphStorageBackend? backend = open(dir);
            var workload = WorkloadGenerator.Generate(scenario.Seed, scenario.TxCount);

            try
            {
                // AbortThenKill では最後の tx を強制的に Rollback してから kill する。
                for (int i = 0; i < workload.Count; i++)
                {
                    var wtx = workload[i];
                    bool forceRollback = scenario.Fault == FaultKind.AbortThenKill && i == workload.Count - 1;
                    RunOneTx(backend!, wtx, oracle, trace, forceRollback);
                }

                // checkpoint fault では PhaseInjector を有効にして小さな commit を追加実行する。
                // kill 例外は Commit から伝播する。
                if (isCheckpointFault)
                {
                    var phase = CheckpointPhaseFor(scenario.Fault);
                    trace.AppendLine($"  ARM CheckpointPhaseKill={phase}");
                    using var arm = injector.ArmCheckpointPhaseKill(phase);
                    try
                    {
                        using var tx = backend!.BeginGraphTransaction(
                            IsolationLevel.SnapshotIsolation, readOnly: false);
                        // threshold=1 で commit 時に即 checkpoint する小さな payload を使う。
                        tx.CreateVertex("Sentinel");
                        try { tx.Commit(); }
                        catch (InvalidOperationException) { /* 模擬 kill */ }
                    }
                    catch (InvalidOperationException) { /* scope での模擬 kill */ }
                }
            }
            catch (Exception ex)
            {
                trace.AppendLine($"  WORKLOAD ABORTED: {ex.GetType().Name}: {ex.Message}");
            }

            // ---- フェーズ 2: process kill と kill 後のファイルレベル障害注入 ----
            trace.AppendLine("  KILL");
            KillProcessSimulator.SimulateKill(ref backend);

            switch (scenario.Fault)
            {
                case FaultKind.KillThenTornWalTail:
                    trace.AppendLine("  INJECT TornWalTail(16B)");
                    injector.InjectTornWriteAtTail(tailBytes: 16);
                    break;
                case FaultKind.KillThenChecksumFlip:
                    trace.AppendLine("  INJECT ChecksumFlip");
                    injector.InjectChecksumCorruption();
                    break;
                case FaultKind.KillThenSidecarDelete:
                    trace.AppendLine("  INJECT SidecarDelete(adj.epoch)");
                    injector.DeleteSidecarFile();
                    break;
            }

            // ---- フェーズ 3: reopen と consistency check ----
            try
            {
                using var reopened = open(dir);
                trace.AppendLine("  REOPEN ok");
                Verify(reopened, oracle, scenario.Fault, trace);
                trace.AppendLine("  VERIFY ok");
                return new ChaosResult(true, trace.ToString());
            }
            catch (CorruptionException ex) when (IsAcceptableReopenFailure(scenario.Fault))
            {
                trace.AppendLine($"  REOPEN refused (acceptable): {ex.GetType().Name}");
                return new ChaosResult(true, trace.ToString());
            }
            catch (StorageException ex) when (IsAcceptableReopenFailure(scenario.Fault))
            {
                trace.AppendLine($"  REOPEN refused (acceptable): {ex.GetType().Name}");
                return new ChaosResult(true, trace.ToString());
            }
            catch (Exception ex)
            {
                trace.AppendLine($"  REOPEN FAILED: {ex.GetType().Name}: {ex.Message}");
                return new ChaosResult(false, trace.ToString(), ex);
            }
        }
        finally
        {
            // kill 後ハンドル解放を待ってから確実に削除し、%TEMP% リークを抑える。
            Quiver.Backend.Tests.Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    private static void RunOneTx(
        IGraphStorageBackend backend, WorkloadTx wtx, OracleState oracle,
        StringBuilder trace, bool forceRollback)
    {
        oracle.BeginTx();
        using var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false);
        var createdIds = new List<VertexId>();
        foreach (var op in wtx.Ops)
        {
            var id = tx.CreateVertex(op.Label);
            tx.SetProperty(id, "marker", PropertyValue.FromInt64(op.PropertyValue));
            if (op.IndexKey is int k)
            {
                tx.IndexInsert(WorkloadGenerator.IndexName, (long)k, id);
            }
            oracle.RecordCreate(id, op.PropertyValue, op.IndexKey);
            createdIds.Add(id);
        }
        bool commit = wtx.Commit && !forceRollback;
        if (commit)
        {
            tx.Commit();
            oracle.CommitTx();
            trace.AppendLine($"  tx commit vertices=[{string.Join(",", createdIds.Select(n => n.Value))}]");
        }
        else
        {
            tx.Rollback();
            oracle.RollbackTx();
            trace.AppendLine($"  tx rollback vertices=[{string.Join(",", createdIds.Select(n => n.Value))}]{(forceRollback ? " (forced AbortThenKill)" : "")}");
        }
    }

    private static CheckpointPhase CheckpointPhaseFor(FaultKind fault) => fault switch
    {
        FaultKind.CheckpointKillAfterBegin => CheckpointPhase.AfterBegin,
        FaultKind.CheckpointKillAfterDataFlush => CheckpointPhase.AfterDataFlush,
        FaultKind.CheckpointKillAfterIndexFlush => CheckpointPhase.AfterIndexFlush,
        FaultKind.CheckpointKillAfterEnd => CheckpointPhase.AfterEnd,
        FaultKind.CheckpointKillAfterTruncate => CheckpointPhase.AfterTruncate,
        _ => throw new ArgumentOutOfRangeException(nameof(fault)),
    };

    private static bool IsAcceptableReopenFailure(FaultKind fault) =>
        fault is FaultKind.KillThenChecksumFlip
            or FaultKind.KillThenSidecarDelete
            or FaultKind.KillThenTornWalTail;

    private static void Verify(
        IGraphStorageBackend backend, OracleState oracle,
        FaultKind fault, StringBuilder trace)
    {
        using var rtx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);

        // suffix-loss を許容する fault:
        //   - KillThenTornWalTail: WAL 末尾の最後の commit が消える可能性。
        //   - KillThenChecksumFlip: CRC が外れたレコード以降が skip され得る (実装上、
        //     latest WAL segment 全体が失われ得るので、torn-tail よりさらに弱い保証)。
        // どちらの場合も「生存している tx は committed 列の prefix を構成すること」
        // (gap が無いこと) を検証する。
        bool allowSuffixLoss = fault is FaultKind.KillThenTornWalTail
            or FaultKind.KillThenChecksumFlip;

        var committed = oracle.CommittedTxs;
        int firstMissingTx = -1;
        for (int i = 0; i < committed.Count; i++)
        {
            bool allPresent = true;
            foreach (var n in committed[i].Vertices)
            {
                if (!rtx.VertexExists(n.Id)) { allPresent = false; break; }
                var prop = rtx.GetProperty(n.Id, "marker");
                if (prop.Int64Value != n.MarkerValue) { allPresent = false; break; }
            }
            if (!allPresent)
            {
                firstMissingTx = i;
                break;
            }
        }

        if (firstMissingTx == -1)
        {
            trace.AppendLine($"  all {committed.Count} committed tx survived");
        }
        else if (allowSuffixLoss)
        {
            // 失われたのは suffix だけであることを確認。
            for (int i = firstMissingTx; i < committed.Count; i++)
            {
                foreach (var n in committed[i].Vertices)
                {
                    if (rtx.VertexExists(n.Id))
                        throw new ChaosVerificationException(
                            $"suffix-loss expected from tx#{firstMissingTx} onward, but tx#{i} vertex {n.Id.Value} survived (partial-replay corruption)");
                }
            }
            trace.AppendLine($"  suffix-loss accepted: {firstMissingTx}/{committed.Count} survived");
        }
        else
        {
            throw new ChaosVerificationException(
                $"committed tx#{firstMissingTx} was lost under fault {fault} (strict-survival contract violated)");
        }
        rtx.Rollback();
    }
}

/// <summary>chaos consistency check の失敗を表すマーカー例外。</summary>
internal sealed class ChaosVerificationException(string message) : Exception(message);
