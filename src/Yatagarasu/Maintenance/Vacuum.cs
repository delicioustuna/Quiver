using System.Diagnostics;
using Yatagarasu.Core;
using Yatagarasu.Telemetry;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;
using Yatagarasu.Storage.Wal;

namespace Yatagarasu.Maintenance;

/// <summary>
/// vacuum 実体。<see cref="BinaryGraphStorageBackend"/> から直接組み立てる。
/// </summary>
/// <remarks>
/// スコープ:
/// <list type="bullet">
///  <item>Edge / プロパティ / Vertexの dead version を物理回収 (順序保証付き)</item>
///  <item>各ストアの free list 圧縮 + 末尾 hwm 縮減</item>
///  <item><see cref="CommittedTxRegistry"/> の visibility horizon を下回ったエントリを prune</item>
/// </list>
/// 後続拡張: B+Tree vertex merge、ページファイル物理 truncate (WAL record 追加が必要)、
/// AutoVacuum バックグラウンドワーカー。
/// </remarks>
internal sealed class Vacuum : IVacuum
{
    private readonly VersionedVertexStore _vertexStore;
    private readonly VersionedEdgeStore _edgeStore;
    private readonly PropertyVersionStore _propStore;
    private readonly TransactionManager _txManager;
    private readonly CommittedTxRegistry _committed;
    private readonly IWriteAheadLog? _wal;
    private readonly VacuumBudget? _budget;
    // nexus 回収に必要な 3 ストア。nexus を持たない構成 (in-memory 等) では null。
    private readonly VersionedNexusStore? _nexusStore;
    private readonly IncidenceStore? _incidenceStore;
    private readonly IVertexIncidenceHeadStore? _vertexHeads;
    private readonly List<long> _reclaimedEdgeSequences = [];

    internal IReadOnlyList<long> ReclaimedEdgeSequences => _reclaimedEdgeSequences;

    internal Vacuum(
        VersionedVertexStore vertexStore,
        VersionedEdgeStore edgeStore,
        PropertyVersionStore propStore,
        TransactionManager txManager,
        CommittedTxRegistry committed,
        IWriteAheadLog? wal = null,
        VersionedNexusStore? nexusStore = null,
        IncidenceStore? incidenceStore = null,
        IVertexIncidenceHeadStore? vertexHeads = null,
        VacuumBudget? budget = null)
    {
        _vertexStore = vertexStore;
        _edgeStore = edgeStore;
        _propStore = propStore;
        _txManager = txManager;
        _committed = committed;
        _wal = wal;
        _nexusStore = nexusStore;
        _incidenceStore = incidenceStore;
        _vertexHeads = vertexHeads;
        _budget = budget;
    }

    public VacuumReport Run(VacuumOptions? options = null)
    {
        options ??= new VacuumOptions();
        var sw = Stopwatch.StartNew();
        var budget = _budget ?? new VacuumBudget(options.MaxDurationMs, TimeProvider.System);

        // vacuum-progress-percent gauge は phase 単位で 0 → 25 → 50 → 75 → 100 と進む。
        // 完了時に 0 へ戻すことで dotnet-counters では「現在実行中か」が判別できる。
        YatagarasuEventSource.Log.SetVacuumProgress(0);
        try
        {
            long horizon = _txManager.GetVisibilityHorizon();
            bool dryRun = options.Mode == VacuumMode.DryRun;

            // 順序:
            //  1. プロパティ（全所有者の先頭参照を更新してからエンティティのスロットを解放する）
            //  2. Edges (同じく vertex の FirstEdgeId 経由で辿る)
            // 3. Vertex
            // committed registry prune は最後 (visibility 判定に依存する処理が全て終わってから)。
            int reclaimedProps = 0;
            if (budget.CanStartPhase())
            {
                reclaimedProps = dryRun
                    ? VacuumProperties(options.Targets, horizon, dryRun: true)
                    : _txManager.ExecuteMaintenanceWrite(() => VacuumProperties(options.Targets, horizon));
            }
            YatagarasuEventSource.Log.SetVacuumProgress(25);

            int reclaimedEdges = 0;
            if ((options.Targets & VacuumTarget.Edges) != 0 && budget.CanStartPhase())
            {
                reclaimedEdges = _edgeStore.VacuumDeadVersions(
                    _vertexStore,
                    horizon,
                    _committed,
                    _reclaimedEdgeSequences,
                    dryRun);
            }
            YatagarasuEventSource.Log.SetVacuumProgress(50);

            int reclaimedVertices = 0;
            if ((options.Targets & VacuumTarget.Vertices) != 0 && budget.CanStartPhase())
            {
                reclaimedVertices = _vertexStore.VacuumDeadVersions(horizon, _committed, dryRun);
            }
            YatagarasuEventSource.Log.SetVacuumProgress(75);

            // プロパティ回収時に先頭参照を更新済み。ここでは接続情報とヘッダだけを回収する。
            int reclaimedNexuses = 0;
            int reclaimedIncidences = 0;
            if ((options.Targets & VacuumTarget.Nexuses) != 0 && budget.CanStartPhase())
            {
                (reclaimedNexuses, reclaimedIncidences) = VacuumNexuses(horizon, dryRun);
            }

            // committed registry を horizon で prune。CompactedVisibilityHorizon を horizon-1 まで進めてから
            // 取り除かないと、データファイル上の xmin がまだ参照する committed tx を「未コミット」と
            // 誤判定してしまう。aborted tx は before-image undo で record ごと消えており、
            // horizon 未満には compact 済みの committed record だけが残る。
            int prunedTxEntries = 0;
            if (budget.CanStartPhase())
            {
                if (!dryRun)
                {
                    long compactedHorizon = horizon - 1;
                    if (compactedHorizon > _committed.CompactedVisibilityHorizon)
                        _committed.CompactedVisibilityHorizon = compactedHorizon;
                }
                prunedTxEntries = _committed.PruneBelow(horizon, dryRun);
            }

            // dead version 回収後に末尾の連続 free page を物理 truncate する。
            // 各ストアの hwm から「必要最小ページ数」を計算し、現在の PageCount より小さければ
            //  1) WAL に FileTruncate を書いて fsync
            //  2) PagedFile.Truncate で MMF unmap → SetLength → remap → meta page 書き戻し
            // を行う。両者の間で crash した場合は recovery の Pass 2 redo が FileTruncate を
            // 再生して冪等に追いつかせる。WAL 未配線 (= テスト経路など) のときは skip。
            long truncatedPages = 0;
            if (!dryRun && _wal != null && budget.CanStartPhase())
            {
                truncatedPages += TryTruncateStore(
                    WalFileKind.Properties,
                    _propStore.UnderlyingFile,
                    _propStore.ComputeRequiredPageCount());
                truncatedPages += TryTruncateStore(
                    WalFileKind.Edges,
                    _edgeStore.UnderlyingFile,
                    _edgeStore.ComputeRequiredPageCount());
                truncatedPages += TryTruncateStore(
                    WalFileKind.Vertices,
                    _vertexStore.UnderlyingFile,
                    _vertexStore.ComputeRequiredPageCount());
            }
            YatagarasuEventSource.Log.SetVacuumProgress(100);

            return new VacuumReport(
                ReclaimedVertices: reclaimedVertices,
                ReclaimedEdges: reclaimedEdges,
                ReclaimedProperties: reclaimedProps,
                PrunedCommittedTxEntries: prunedTxEntries,
                ElapsedMs: sw.ElapsedMilliseconds,
                HorizonTxId: horizon,
                Skipped: false,
                TruncatedPages: truncatedPages,
                ReclaimedNexuses: reclaimedNexuses,
                ReclaimedIncidences: reclaimedIncidences);
        }
        finally
        {
            YatagarasuEventSource.Log.SetVacuumProgress(0);
        }
    }

    /// <summary>
    /// 全所有者のプロパティをエンティティ回収前に処理する。エンティティのみを対象とする場合は、削除済み所有者に従属するデータだけを回収する。
    /// </summary>
    private int VacuumProperties(VacuumTarget targets, long horizon, bool dryRun = false)
    {
        bool allProperties = (targets & VacuumTarget.Properties) != 0;
        int reclaimed = 0;
        var context = new PropertyVersionStore.PropertyVacuumContext();
        void Reclaim(EntityRef owner, PropertyVersionRef head, long xmax, VacuumTarget entityTarget)
        {
            bool dead = xmax != 0 && xmax < horizon && _committed.IsCommitted(xmax);
            if (!head.IsValid || !allProperties && (!dead || (targets & entityTarget) == 0)) return;
            var plan = _propStore.PlanVacuum(owner, head, dead, horizon, _committed);
            if (plan.Reclaimed.Count == 0) return;
            if (dryRun)
            {
                reclaimed += plan.Reclaimed.Count;
                return;
            }
            var newHead = _propStore.ApplyVacuum(plan, context);
            switch (owner.Kind)
            {
                case EntityKind.Vertex:
                    _vertexStore.UpdateFirstPropertyRefRaw(new VertexId(owner.Value), newHead);
                    break;
                case EntityKind.Edge:
                    _edgeStore.UpdateFirstPropertyRefRaw(new EdgeId(owner.Value), newHead);
                    break;
                case EntityKind.Nexus:
                    var write = _nexusStore!.Write(new NexusId(owner.Value));
                    try { write.FirstPropertyRef = newHead; }
                    finally { write.Dispose(); }
                    break;
            }
            reclaimed += plan.Reclaimed.Count;
        }

        if (allProperties || (targets & VacuumTarget.Vertices) != 0)
            for (long sequence = 0; sequence < _vertexStore.Hwm; sequence++)
            {
                var raw = _vertexStore.ReadRaw(sequence);
                if (raw.InUse)
                    Reclaim(EntityRef.From(VertexId.Create(sequence, _vertexStore.CurrentGeneration(sequence))),
                        raw.FirstPropertyRef, raw.Xmax, VacuumTarget.Vertices);
            }
        if (allProperties || (targets & VacuumTarget.Edges) != 0)
            for (long sequence = 0; sequence < _edgeStore.Hwm; sequence++)
            {
                var raw = _edgeStore.ReadRaw(sequence);
                if (raw.InUse)
                    Reclaim(EntityRef.From(EdgeId.Create(sequence, _edgeStore.CurrentGeneration(sequence))),
                        raw.FirstPropertyRef, raw.Xmax, VacuumTarget.Edges);
            }
        if (_nexusStore is not null && (allProperties || (targets & VacuumTarget.Nexuses) != 0))
            for (long sequence = 0; sequence < _nexusStore.SequenceHighWaterMark; sequence++)
                if (_nexusStore.TryReadRawHeader(sequence, out var raw) && raw.InUse)
                    Reclaim(EntityRef.From(NexusId.Create(sequence, _nexusStore.CurrentGeneration(sequence))),
                        raw.FirstProperty, raw.Xmax, VacuumTarget.Nexuses);
        if (!dryRun && reclaimed > 0) _propStore.FinishExternalReclaim();
        return reclaimed;
    }

    /// <summary>プロパティ回収後、頂点ごとの走査で接続情報を切り離し、最後に削除済みヘッダを回収する。</summary>
    private (int Nexuses, int Incidences) VacuumNexuses(long horizon, bool dryRun = false)
    {
        // nexus を持たない構成では 3 ストアが揃わない。安全側で何もしない。
        if (_nexusStore is null || _incidenceStore is null || _vertexHeads is null)
            return (0, 0);

        var deadHeaders = new List<long>();
        var deadIncidences = new HashSet<long>();
        // dead incidence を「影響 vertex の sequence」でグループ化し、chain sweep 対象 vertex を一意化する。
        var affectedVertices = new HashSet<long>();

        long hwm = _nexusStore.SequenceHighWaterMark;
        for (long seq = 0; seq < hwm; seq++)
        {
            if (!_nexusStore.TryReadRawHeader(seq, out RawNexusHeader raw))
                continue;
            bool dead = raw.Xmax != 0 && raw.Xmax < horizon && _committed.IsCommitted(raw.Xmax);
            if (dead)
            {
                deadHeaders.Add(seq);

                // nexus chain (NextInNexus) を辿って所属 incidence を集める。header は
                // まだ heap 上にあり、incidence slot も未解放なので安全に走査できる。
                IncidenceId cur = raw.FirstIncidence;
                long guard = _incidenceStore.SequenceHighWaterMark + 1;
                while (cur.IsValid && guard-- > 0)
                {
                    using IncidenceReadHandle inc = _incidenceStore.Read(cur);
                    if (!inc.InUse)
                        break;
                    if (deadIncidences.Add(cur.Sequence))
                        affectedVertices.Add(inc.VertexId.Sequence);
                    cur = inc.NextInNexus;
                }
            }
            else if (!dryRun && raw.InUse && raw.Xmax == 0)
            {
                _nexusStore.PruneDeadHeaderVersions(seq, horizon, _committed);
            }
        }

        if (dryRun) return (deadHeaders.Count, deadIncidences.Count);
        if (deadHeaders.Count == 0)
            return (0, 0);

        // 影響 vertex ごとに 1 パスで dead incidence を chain から外す。
        foreach (long vertexSeq in affectedVertices)
            SweepVertexChain(vertexSeq, deadIncidences);

        // unlink 済み slot を free list へ返す。
        foreach (long incSeq in deadIncidences)
            _incidenceStore.Free(new IncidenceId(incSeq));

        // header を物理回収する (property → incidence の後、最後に header)。
        foreach (long seq in deadHeaders)
            _nexusStore.ReclaimHeader(seq);

        return (deadHeaders.Count, deadIncidences.Count);
    }

    /// <summary>
    /// 1 つの vertex の incidence chain を head から走査し、<paramref name="deadSet"/> に含まれる
    /// incidence を running prev で一括 unlink する。head 自体が dead の場合は vertex head を
    /// 次の生存 incidence へ進める。逆リンクを持たない前提での O(chain 長) sweep。
    /// </summary>
    private void SweepVertexChain(long vertexSeq, HashSet<long> deadSet)
    {
        var vertex = new VertexId(vertexSeq);
        IncidenceId prev = IncidenceId.Invalid;
        IncidenceId cur = _vertexHeads!.Get(vertex);
        long guard = _incidenceStore!.SequenceHighWaterMark + 1;

        while (cur.IsValid && guard-- > 0)
        {
            IncidenceId next;
            bool dead;
            using (IncidenceReadHandle inc = _incidenceStore.Read(cur))
            {
                if (!inc.InUse)
                    break;
                next = inc.NextInVertex;
                dead = deadSet.Contains(cur.Sequence);
            }

            if (dead)
            {
                // dead を chain から外す。head 位置なら vertex head を前進、途中なら
                // 直前の生存 incidence の nextInVertex を後続へ繋ぎ替える。prev は進めない。
                if (!prev.IsValid)
                    _vertexHeads.Set(vertex, next);
                else
                {
                    IncidenceWriteHandle w = _incidenceStore.Write(prev);
                    w.NextInVertex = next;
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
