using System.Diagnostics;
using Quiver.Core;
using Quiver.Telemetry;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Quiver.Storage.Wal;

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
    private readonly VersionedNodeStore _nodeStore;
    private readonly VersionedRelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    private readonly TransactionManager _txManager;
    private readonly CommittedTxRegistry _committed;
    private readonly IWriteAheadLog? _wal;
    // ARCH-5c Phase 5e: opt-in 列の delta compaction 対象 (列無し DB では null)。
    private readonly ColumnManager? _columns;

    internal Vacuum(
        VersionedNodeStore nodeStore,
        VersionedRelationshipStore relStore,
        PropertyStore propStore,
        TransactionManager txManager,
        CommittedTxRegistry committed,
        IWriteAheadLog? wal = null,
        ColumnManager? columns = null)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _txManager = txManager;
        _committed = committed;
        _wal = wal;
        _columns = columns;
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

            // ARCH-5c Phase 5e: opt-in 列の delta compaction。committed registry の prune より前に
            // 走らせる (Merge は committed.IsCommitted を見るため、prune で presumed-committed 化される前に
            // 判定する必要がある — dead version 回収と同じ順序制約)。targets に依らず常に実行する
            // (in-memory delta の merge は安価で常に有益)。
            int reclaimedColumnVersions = 0;
            if (!dryRun && _columns != null)
            {
                reclaimedColumnVersions = _columns.Compact(horizon, _committed);
            }

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

            // OP-5: dead version 回収後に末尾の連続 free page を物理 truncate する。
            // 各ストアの hwm から「必要最小ページ数」を計算し、現在の PageCount より小さければ
            //   1) WAL に FileTruncate を書いて fsync
            //   2) PagedFile.Truncate で MMF unmap → SetLength → remap → meta page 書き戻し
            // を行う。両者の間で crash した場合は recovery の Pass 2 redo が FileTruncate を
            // 再生して冪等に追いつかせる。WAL 未配線 (= テスト経路など) のときは skip。
            long truncatedPages = 0;
            if (!dryRun && _wal != null)
            {
                truncatedPages += TryTruncateStore(
                    WalFileKind.Properties,
                    _propStore.UnderlyingFile,
                    _propStore.ComputeRequiredPageCount());
                truncatedPages += TryTruncateStore(
                    WalFileKind.Relationships,
                    _relStore.UnderlyingFile,
                    _relStore.ComputeRequiredPageCount());
                truncatedPages += TryTruncateStore(
                    WalFileKind.Nodes,
                    _nodeStore.UnderlyingFile,
                    _nodeStore.ComputeRequiredPageCount());
            }
            QuiverEventSource.Log.SetVacuumProgress(100);

            return new VacuumReport(
                ReclaimedNodes: reclaimedNodes,
                ReclaimedRelationships: reclaimedRels,
                ReclaimedProperties: reclaimedProps,
                PrunedCommittedTxEntries: prunedTxEntries,
                ElapsedMs: sw.ElapsedMilliseconds,
                HorizonTxId: horizon,
                Skipped: false,
                TruncatedPages: truncatedPages,
                ReclaimedColumnVersions: reclaimedColumnVersions);
        }
        finally
        {
            QuiverEventSource.Log.SetVacuumProgress(0);
        }
    }

    private long TryTruncateStore(WalFileKind kind, IPagedFile file, long newPageCount)
    {
        long current = file.PageCount;
        if (newPageCount < 1) newPageCount = 1;
        if (newPageCount >= current) return 0;
        // 順序が重要:
        //   1. WAL に FileTruncate を書いて fsync。これより前に物理 truncate が起きると
        //      recovery が「pageId が存在しない」と読み損なう可能性があるが、
        //      実際の Truncate は (2) なので不変条件は維持される。
        //   2. PagedFile.Truncate で MMF unmap → SetLength → meta page 書き戻し → fsync。
        // (1) と (2) の間で crash しても、recovery Pass 2 redo が FileTruncate を再生して
        // 物理 file が再 truncate される (PagedFile.Truncate は newPageCount >= 現状 は no-op)。
        // ARCH-4: TenantPagedFile (単一ファイルコンテナ) の truncate はテナント論理空間の縮小 +
        // 物理ページのグローバル free list 返却で、自身で flush して durable 化する (物理ファイルは
        // 縮まない)。WAL FileTruncate は per-store 物理ファイルの物理 truncate 冪等再生用なので、
        // テナントに対しては書かない (書くと recovery が fileKind を container.Physical に誤マップして
        // 全体を物理 truncate しうる)。
        if (file is not TenantPagedFile)
            _wal!.WriteFileTruncate((byte)kind, newPageCount);
        file.Truncate(newPageCount);
        return current - newPageCount;
    }
}
