using System.Diagnostics;
using Quiver.Core;
using Quiver.Telemetry;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Quiver.Storage.Wal;

namespace Quiver.Maintenance;

/// <summary>
/// vacuum 実体。<see cref="BinaryGraphStorageBackend"/> から直接組み立てる。
/// </summary>
/// <remarks>
/// スコープ:
/// <list type="bullet">
///  <item>リレーションシップ / プロパティ / ノードの dead version を物理回収 (順序保証付き)</item>
///  <item>各ストアの free list 圧縮 + 末尾 hwm 縮減</item>
///  <item><see cref="CommittedTxRegistry"/> の visibility horizon を下回ったエントリを prune</item>
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
    // opt-in 列の delta compaction 対象 (列無し DB では null)。
    private readonly ColumnManager? _columns;
    // hyperedge 回収に必要な 3 ストア。hyperedge を持たない構成 (in-memory 等) では null。
    private readonly VersionedHyperedgeStore? _hyperedgeStore;
    private readonly IncidenceStore? _incidenceStore;
    private readonly INodeIncidenceHeadStore? _nodeHeads;

    internal Vacuum(
        VersionedNodeStore nodeStore,
        VersionedRelationshipStore relStore,
        PropertyStore propStore,
        TransactionManager txManager,
        CommittedTxRegistry committed,
        IWriteAheadLog? wal = null,
        ColumnManager? columns = null,
        VersionedHyperedgeStore? hyperedgeStore = null,
        IncidenceStore? incidenceStore = null,
        INodeIncidenceHeadStore? nodeHeads = null)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _txManager = txManager;
        _committed = committed;
        _wal = wal;
        _columns = columns;
        _hyperedgeStore = hyperedgeStore;
        _incidenceStore = incidenceStore;
        _nodeHeads = nodeHeads;
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

        // vacuum-progress-percent gauge は phase 単位で 0 → 25 → 50 → 75 → 100 と進む。
        // 完了時に 0 へ戻すことで dotnet-counters では「現在実行中か」が判別できる。
        QuiverEventSource.Log.SetVacuumProgress(0);
        try
        {
            long horizon = _txManager.GetVisibilityHorizon();
            bool dryRun = options.Mode == VacuumMode.DryRun;

            // 順序:
            //  1. Properties (dead ノードの prop chain は node.FirstPropId 経由でしか辿れないので、
            //     node vacuum 前に処理する必要がある)
            //  2. Relationships (同じく node の FirstRelId 経由で辿る)
            // 3. ノード
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

            // dead hyperedge の property → incidence → header を回収する。node vacuum は
            // hyperedge の overflow property を辿らないため、この phase が独立して解放する。
            // 回収した overflow property は ReclaimedProperties へ合算する。
            int reclaimedHyperedges = 0;
            int reclaimedIncidences = 0;
            if (!dryRun && (options.Targets & VacuumTarget.Hyperedges) != 0)
            {
                int hyperedgeProps;
                (reclaimedHyperedges, reclaimedIncidences, hyperedgeProps) = VacuumHyperedges(horizon);
                reclaimedProps += hyperedgeProps;
            }

            // opt-in 列の delta compaction。committed registry の prune より前に
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

            // dead version 回収後に末尾の連続 free page を物理 truncate する。
            // 各ストアの hwm から「必要最小ページ数」を計算し、現在の PageCount より小さければ
            //  1) WAL に FileTruncate を書いて fsync
            //  2) PagedFile.Truncate で MMF unmap → SetLength → remap → meta page 書き戻し
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
                ReclaimedColumnVersions: reclaimedColumnVersions,
                ReclaimedHyperedges: reclaimedHyperedges,
                ReclaimedIncidences: reclaimedIncidences);
        }
        finally
        {
            QuiverEventSource.Log.SetVacuumProgress(0);
        }
    }

    /// <summary>
    /// dead hyperedge を回収する。手順は property → incidence → header の順:
    /// <list type="number">
    ///   <item>全 header slot を走査し、horizon 未満で commit 済みの xmax を持つ dead header を集める。
    ///         その overflow property chain をここで解放し、hyperedge chain を辿って所属 incidence を
    ///         node 別に集める。live header は inline property の copy-on-write 旧版を prune する。</item>
    ///   <item>影響を受けた各 node の chain を head から 1 回だけ走査し、running prev で dead incidence を
    ///         一括 unlink する。逆リンクを持たないため個別 unlink はせず、node ごと O(chain 長) で済む。</item>
    ///   <item>unlink 済み incidence slot を free list へ返す (active tx が無いことは <see cref="Run"/> が保証)。</item>
    ///   <item>header を heap から物理回収し sequence を free list へ返す (再利用時に世代 +1)。</item>
    /// </list>
    /// </summary>
    /// <returns>回収した (hyperedge header 数, incidence 数, overflow property 版数)。</returns>
    private (int Hyperedges, int Incidences, int Properties) VacuumHyperedges(long horizon)
    {
        // hyperedge を持たない構成では 3 ストアが揃わない。安全側で何もしない。
        if (_hyperedgeStore is null || _incidenceStore is null || _nodeHeads is null)
            return (0, 0, 0);

        var deadHeaders = new List<long>();
        var deadIncidences = new HashSet<long>();
        // dead incidence を「影響 node の sequence」でグループ化し、chain sweep 対象 node を一意化する。
        var affectedNodes = new HashSet<long>();
        int reclaimedProps = 0;

        long hwm = _hyperedgeStore.SequenceHighWaterMark;
        for (long seq = 0; seq < hwm; seq++)
        {
            if (!_hyperedgeStore.TryReadRawHeader(seq, out RawHyperedgeHeader raw))
                continue;
            bool dead = raw.Xmax != 0 && raw.Xmax < horizon && _committed.IsCommitted(raw.Xmax);
            if (dead)
            {
                deadHeaders.Add(seq);

                // property を先に解放する。Invalid head は 0 件で返る。
                reclaimedProps += _propStore.ReclaimOverflowChain(raw.FirstProperty);

                // hyperedge chain (NextInHyperedge) を辿って所属 incidence を集める。header は
                // まだ heap 上にあり、incidence slot も未解放なので安全に走査できる。
                IncidenceId cur = raw.FirstIncidence;
                long guard = _incidenceStore.SequenceHighWaterMark + 1;
                while (cur.IsValid && guard-- > 0)
                {
                    using IncidenceReadHandle inc = _incidenceStore.Read(cur);
                    if (!inc.InUse)
                        break;
                    if (deadIncidences.Add(cur.Sequence))
                        affectedNodes.Add(inc.NodeId.Sequence);
                    cur = inc.NextInHyperedge;
                }
            }
            else if (raw.InUse && raw.Xmax == 0)
            {
                _hyperedgeStore.PruneDeadInlineVersions(seq, horizon, _committed);
            }
        }

        if (deadHeaders.Count == 0)
            return (0, 0, 0);

        // 影響 node ごとに 1 パスで dead incidence を chain から外す。
        foreach (long nodeSeq in affectedNodes)
            SweepNodeChain(nodeSeq, deadIncidences);

        // unlink 済み slot を free list へ返す。
        foreach (long incSeq in deadIncidences)
            _incidenceStore.Free(new IncidenceId(incSeq));

        // header を物理回収する (property → incidence の後、最後に header)。
        foreach (long seq in deadHeaders)
            _hyperedgeStore.ReclaimHeader(seq);

        if (reclaimedProps > 0)
            _propStore.FinishExternalReclaim();

        return (deadHeaders.Count, deadIncidences.Count, reclaimedProps);
    }

    /// <summary>
    /// 1 つの node の incidence chain を head から走査し、<paramref name="deadSet"/> に含まれる
    /// incidence を running prev で一括 unlink する。head 自体が dead の場合は node head を
    /// 次の生存 incidence へ進める。逆リンクを持たない前提での O(chain 長) sweep。
    /// </summary>
    private void SweepNodeChain(long nodeSeq, HashSet<long> deadSet)
    {
        var node = new NodeId(nodeSeq);
        IncidenceId prev = IncidenceId.Invalid;
        IncidenceId cur = _nodeHeads!.Get(node);
        long guard = _incidenceStore!.SequenceHighWaterMark + 1;

        while (cur.IsValid && guard-- > 0)
        {
            IncidenceId next;
            bool dead;
            using (IncidenceReadHandle inc = _incidenceStore.Read(cur))
            {
                if (!inc.InUse)
                    break;
                next = inc.NextInNode;
                dead = deadSet.Contains(cur.Sequence);
            }

            if (dead)
            {
                // dead を chain から外す。head 位置なら node head を前進、途中なら
                // 直前の生存 incidence の nextInNode を後続へ繋ぎ替える。prev は進めない。
                if (!prev.IsValid)
                    _nodeHeads.Set(node, next);
                else
                {
                    IncidenceWriteHandle w = _incidenceStore.Write(prev);
                    w.NextInNode = next;
                    w.Dispose();
                }
            }
            else
            {
                prev = cur;
            }

            cur = next;
        }
    }

    private long TryTruncateStore(WalFileKind kind, IPagedFile file, long newPageCount)
    {
        long current = file.PageCount;
        if (newPageCount < 1) newPageCount = 1;
        if (newPageCount >= current) return 0;
        // 順序が重要:
        //  1. WAL に FileTruncate を書いて fsync。これより前に物理 truncate が起きると
        //     recovery が「pageId が存在しない」と読み損なう可能性があるが、
        //     実際の Truncate は (2) なので不変条件は維持される。
        //  2. PagedFile.Truncate で MMF unmap → SetLength → meta page 書き戻し → fsync。
        // (1) と (2) の間で crash しても、recovery Pass 2 redo が FileTruncate を再生して
        // 物理 file が再 truncate される (PagedFile.Truncate は newPageCount >= 現状 は no-op)。
        // TenantPagedFile (単一ファイルコンテナ) の truncate はテナント論理空間の縮小 +
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
