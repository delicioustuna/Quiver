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
        IVertexIncidenceHeadStore? vertexHeads = null)
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
    }

    public VacuumReport Run(VacuumOptions? options = null)
    {
        options ??= new VacuumOptions();
        var sw = Stopwatch.StartNew();

        // vacuum-progress-percent gauge は phase 単位で 0 → 25 → 50 → 75 → 100 と進む。
        // 完了時に 0 へ戻すことで dotnet-counters では「現在実行中か」が判別できる。
        QuiverEventSource.Log.SetVacuumProgress(0);
        try
        {
            long horizon = _txManager.GetVisibilityHorizon();
            bool dryRun = options.Mode == VacuumMode.DryRun;

            // 順序:
            //  1. Properties (dead Vertexの prop chain は vertex.FirstPropertyRef 経由でしか辿れないので、
            //     vertex vacuum 前に処理する必要がある)
            //  2. Edges (同じく vertex の FirstEdgeId 経由で辿る)
            // 3. Vertex
            // committed registry prune は最後 (visibility 判定に依存する処理が全て終わってから)。
            int reclaimedProps = 0;
            if (!dryRun && (options.Targets & VacuumTarget.Properties) != 0)
            {
                reclaimedProps = _propStore.VacuumDeadVersions(_vertexStore, horizon, _committed);
            }
            QuiverEventSource.Log.SetVacuumProgress(25);

            int reclaimedEdges = 0;
            if (!dryRun && (options.Targets & VacuumTarget.Edges) != 0)
            {
                reclaimedEdges = _edgeStore.VacuumDeadVersions(
                    _vertexStore,
                    horizon,
                    _committed,
                    _reclaimedEdgeSequences);
            }
            QuiverEventSource.Log.SetVacuumProgress(50);

            int reclaimedVertices = 0;
            if (!dryRun && (options.Targets & VacuumTarget.Vertices) != 0)
            {
                reclaimedVertices = _vertexStore.VacuumDeadVersions(horizon, _committed);
            }
            QuiverEventSource.Log.SetVacuumProgress(75);

            // dead nexus の property → incidence → header を回収する。vertex vacuum は
            // nexus の overflow property を辿らないため、この phase が独立して解放する。
            // 回収した overflow property は ReclaimedProperties へ合算する。
            int reclaimedNexuses = 0;
            int reclaimedIncidences = 0;
            if (!dryRun && (options.Targets & VacuumTarget.Nexuses) != 0)
            {
                int nexusProps;
                (reclaimedNexuses, reclaimedIncidences, nexusProps) = VacuumNexuses(horizon);
                reclaimedProps += nexusProps;
            }

            // committed registry を horizon で prune。CompactedVisibilityHorizon を horizon-1 まで進めてから
            // 取り除かないと、データファイル上の xmin がまだ参照する committed tx を「未コミット」と
            // 誤判定してしまう。aborted tx は before-image undo で record ごと消えており、
            // horizon 未満には compact 済みの committed record だけが残る。
            int prunedTxEntries = 0;
            if (!dryRun)
            {
                long compactedHorizon = horizon - 1;
                if (compactedHorizon > _committed.CompactedVisibilityHorizon)
                    _committed.CompactedVisibilityHorizon = compactedHorizon;
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
                    WalFileKind.Edges,
                    _edgeStore.UnderlyingFile,
                    _edgeStore.ComputeRequiredPageCount());
                truncatedPages += TryTruncateStore(
                    WalFileKind.Vertices,
                    _vertexStore.UnderlyingFile,
                    _vertexStore.ComputeRequiredPageCount());
            }
            QuiverEventSource.Log.SetVacuumProgress(100);

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
            QuiverEventSource.Log.SetVacuumProgress(0);
        }
    }

    /// <summary>
    /// dead nexus を回収する。手順は property → incidence → header の順:
    /// <list type="number">
    ///   <item>全 header slot を走査し、horizon 未満で commit 済みの xmax を持つ dead header を集める。
    ///         その overflow property chain をここで解放し、nexus chain を辿って所属 incidence を
    ///         vertex 別に集める。live header は inline property の copy-on-write 旧版を prune する。</item>
    ///   <item>影響を受けた各 vertex の chain を head から 1 回だけ走査し、running prev で dead incidence を
    ///         一括 unlink する。逆リンクを持たないため個別 unlink はせず、vertex ごと O(chain 長) で済む。</item>
    ///   <item>unlink 済み incidence slot を free list へ返す。horizon より新しい snapshot 可視版は対象外。</item>
    ///   <item>header を heap から物理回収し sequence を free list へ返す (再利用時に世代 +1)。</item>
    /// </list>
    /// </summary>
    /// <returns>回収した (nexus header 数, incidence 数, overflow property 版数)。</returns>
    private (int Nexuses, int Incidences, int Properties) VacuumNexuses(long horizon)
    {
        // nexus を持たない構成では 3 ストアが揃わない。安全側で何もしない。
        if (_nexusStore is null || _incidenceStore is null || _vertexHeads is null)
            return (0, 0, 0);

        var deadHeaders = new List<long>();
        var deadIncidences = new HashSet<long>();
        // dead incidence を「影響 vertex の sequence」でグループ化し、chain sweep 対象 vertex を一意化する。
        var affectedVertices = new HashSet<long>();
        int reclaimedProps = 0;

        long hwm = _nexusStore.SequenceHighWaterMark;
        for (long seq = 0; seq < hwm; seq++)
        {
            if (!_nexusStore.TryReadRawHeader(seq, out RawNexusHeader raw))
                continue;
            bool dead = raw.Xmax != 0 && raw.Xmax < horizon && _committed.IsCommitted(raw.Xmax);
            if (dead)
            {
                deadHeaders.Add(seq);

                // property を先に解放する。Invalid head は 0 件で返る。
                reclaimedProps += _propStore.ReclaimOverflowChain(raw.FirstProperty);

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
            else if (raw.InUse && raw.Xmax == 0)
            {
                _nexusStore.PruneDeadInlineVersions(seq, horizon, _committed);
            }
        }

        if (deadHeaders.Count == 0)
            return (0, 0, 0);

        // 影響 vertex ごとに 1 パスで dead incidence を chain から外す。
        foreach (long vertexSeq in affectedVertices)
            SweepVertexChain(vertexSeq, deadIncidences);

        // unlink 済み slot を free list へ返す。
        foreach (long incSeq in deadIncidences)
            _incidenceStore.Free(new IncidenceId(incSeq));

        // header を物理回収する (property → incidence の後、最後に header)。
        foreach (long seq in deadHeaders)
            _nexusStore.ReclaimHeader(seq);

        if (reclaimedProps > 0)
            _propStore.FinishExternalReclaim();

        return (deadHeaders.Count, deadIncidences.Count, reclaimedProps);
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
