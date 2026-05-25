using System.Diagnostics;
using Quiver.Core;
using Quiver.Core.Telemetry;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Maintenance;

/// <summary>
/// OP-3 vacuum 実体。<see cref="BinaryGraphStorageBackend"/> から直接組み立てる。
/// </summary>
/// <remarks>
/// スコープ:
/// <list type="bullet">
///   <item>リレーションシップ / プロパティ / ノードの dead version を物理回収 (順序保証付き)</item>
///   <item>各ストアの free list 圧縮 + 末尾 hwm 縮減</item>
///   <item><see cref="CommittedTxRegistry"/> の visibility horizon を下回ったエントリを prune</item>
/// </list>
/// 後続拡張: B+Tree node merge、ページファイル物理 truncate (WAL record 追加が必要)、
/// AutoVacuum バックグラウンドワーカー。
/// </remarks>
internal sealed class Vacuum : IVacuum
{
    private readonly NodeStore _nodeStore;
    private readonly RelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    private readonly TransactionManager _txManager;
    private readonly CommittedTxRegistry _committed;

    internal Vacuum(
        NodeStore nodeStore,
        RelationshipStore relStore,
        PropertyStore propStore,
        TransactionManager txManager,
        CommittedTxRegistry committed)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _txManager = txManager;
        _committed = committed;
    }

    public VacuumReport Run(VacuumOptions? options = null)
    {
        options ??= new VacuumOptions();
        var sw = Stopwatch.StartNew();

        if (_txManager.ActiveCount > 0)
        {
            return new VacuumReport(
                ReclaimedNodes: 0,
                ReclaimedRelationships: 0,
                ReclaimedProperties: 0,
                PrunedCommittedTxEntries: 0,
                ElapsedMs: sw.ElapsedMilliseconds,
                HorizonTxId: 0,
                Skipped: true);
        }

        // OB-2: vacuum-progress-percent gauge は phase 単位で 0 → 25 → 50 → 75 → 100 と進む。
        // 完了時に 0 へ戻すことで dotnet-counters では「現在実行中か」が判別できる。
        QuiverEventSource.Log.SetVacuumProgress(0);
        try
        {
            long horizon = _txManager.GetVisibilityHorizon();
            bool dryRun = options.Mode == VacuumMode.DryRun;

            // 順序:
            //   1. Properties (dead ノードの prop chain は node.FirstPropId 経由でしか辿れないので、
            //      node vacuum 前に処理する必要がある)
            //   2. Relationships (同じく node の FirstRelId 経由で辿る)
            //   3. Nodes
            // committed registry prune は最後 (visibility 判定に依存する処理が全て終わってから)。
            int reclaimedProps = 0;
            if (!dryRun && (options.Targets & VacuumTarget.Properties) != 0)
            {
                reclaimedProps = _propStore.VacuumDeadVersions(_nodeStore, horizon, _committed);
            }
            QuiverEventSource.Log.SetVacuumProgress(25);

            int reclaimedRels = 0;
            if (!dryRun && (options.Targets & VacuumTarget.Relationships) != 0)
            {
                reclaimedRels = _relStore.VacuumDeadVersions(_nodeStore, horizon, _committed);
            }
            QuiverEventSource.Log.SetVacuumProgress(50);

            int reclaimedNodes = 0;
            if (!dryRun && (options.Targets & VacuumTarget.Nodes) != 0)
            {
                reclaimedNodes = _nodeStore.VacuumDeadVersions(horizon, _committed);
            }
            QuiverEventSource.Log.SetVacuumProgress(75);

            // committed registry を horizon で prune。RecoveryHorizon を horizon-1 まで進めてから
            // 取り除かないと、データファイル上の xmin がまだ参照する committed tx を「未コミット」と
            // 誤判定してしまう。aborted tx は before-image undo で record ごと消えるため、horizon 未満の
            // 全 tx を presumed-committed として扱っても correctness は崩れない。
            int prunedTxEntries = 0;
            if (!dryRun)
            {
                long newRecoveryHorizon = horizon - 1;
                if (newRecoveryHorizon > _committed.RecoveryHorizon)
                    _committed.RecoveryHorizon = newRecoveryHorizon;
                prunedTxEntries = _committed.PruneBelow(horizon);
            }

            return new VacuumReport(
                ReclaimedNodes: reclaimedNodes,
                ReclaimedRelationships: reclaimedRels,
                ReclaimedProperties: reclaimedProps,
                PrunedCommittedTxEntries: prunedTxEntries,
                ElapsedMs: sw.ElapsedMilliseconds,
                HorizonTxId: horizon,
                Skipped: false);
        }
        finally
        {
            QuiverEventSource.Log.SetVacuumProgress(0);
        }
    }
}
