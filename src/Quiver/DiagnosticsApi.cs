using Quiver.Core;
using Quiver.Telemetry;
using Quiver.Index;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

internal sealed class DiagnosticsApi : IDiagnosticsApi
{
    private readonly INodeStore _nodeStore;
    private readonly IRelationshipStore _relStore;
    private readonly IHyperedgeStore _hyperedgeStore;
    private readonly IIncidenceStore _incidenceStore;
    private readonly INodeIncidenceHeadStore _nodeIncidenceHeads;
    private readonly IGraphAccessMethods _access;
    // orphan 検出は型を意識せずに全索引を走査する必要があるため
    // IIndexManager の non-generic 経路を直接持つ。
    private readonly IndexManager? _indexManager;
    private readonly LabelNodeIndex? _labelIndex;
    // Adaptive checkpoint controller の現在 threshold と policy 切り替えを公開する経路。
    private readonly TransactionManager? _txManager;
    // SetCheckpointPolicy(Adaptive, ...) で新規 controller を構築するための保存値。
    private readonly TimeSpan _adaptiveTargetRecoveryTime;
    private readonly long _adaptiveMinThresholdBytes;
    private readonly long _adaptiveMaxThresholdBytes;
    private readonly int _adaptiveSampleWindow;

    internal DiagnosticsApi(
        INodeStore nodeStore,
        IRelationshipStore relStore,
        IGraphAccessMethods access,
        IHyperedgeStore? hyperedgeStore = null,
        IIncidenceStore? incidenceStore = null,
        INodeIncidenceHeadStore? nodeIncidenceHeads = null,
        IndexManager? indexManager = null,
        LabelNodeIndex? labelIndex = null,
        TransactionManager? txManager = null,
        TimeSpan? adaptiveTargetRecoveryTime = null,
        long adaptiveMinThresholdBytes = 4L * 1024 * 1024,
        long adaptiveMaxThresholdBytes = 1024L * 1024 * 1024,
        int adaptiveSampleWindow = 1000)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _hyperedgeStore = hyperedgeStore ?? NullHyperedgeStore.Instance;
        _incidenceStore = incidenceStore ?? NullIncidenceStore.Instance;
        _nodeIncidenceHeads = nodeIncidenceHeads ?? NullNodeIncidenceHeadStore.Instance;
        _access = access;
        _indexManager = indexManager;
        _labelIndex = labelIndex;
        _txManager = txManager;
        _adaptiveTargetRecoveryTime = adaptiveTargetRecoveryTime ?? TimeSpan.FromSeconds(5);
        _adaptiveMinThresholdBytes = adaptiveMinThresholdBytes;
        _adaptiveMaxThresholdBytes = adaptiveMaxThresholdBytes;
        _adaptiveSampleWindow = adaptiveSampleWindow;
    }

    public long CurrentCheckpointThresholdBytes
        => _txManager?.CurrentCheckpointThresholdBytes ?? 0;

    public void SetCheckpointPolicy(CheckpointPolicy policy, long? fixedThresholdBytes = null)
    {
        if (_txManager == null) return;
        long initial = fixedThresholdBytes ?? _txManager.CurrentCheckpointThresholdBytes;
        if (initial <= 0) initial = 64L * 1024 * 1024;
        if (policy == CheckpointPolicy.Fixed)
        {
            _txManager.SetAdaptiveController(null);
            _txManager.SetFixedThreshold(initial);
        }
        else
        {
            _txManager.SetFixedThreshold(initial);
            var controller = new AdaptiveCheckpointController(
                initial,
                _adaptiveTargetRecoveryTime,
                _adaptiveMinThresholdBytes,
                _adaptiveMaxThresholdBytes,
                _adaptiveSampleWindow);
            _txManager.SetAdaptiveController(controller);
        }
    }

    public DatabaseStatistics GetStatistics() => new(
        NodeCount: _nodeStore.InUseCount,
        RelationshipCount: _relStore.InUseCount,
        HyperedgeCount: _hyperedgeStore.InUseCount,
        IncidenceCount: _incidenceStore.InUseCount,
        PropertyCount: 0,
        DataFileSize: 0,
        WalFileSize: 0,
        BufferPoolHits: 0,
        BufferPoolMisses: 0,
        AdjacencyFallbackCount: _access.AdjacencyFallbackCount);

    public ConsistencyReport CheckConsistency()
    {
        var issues = new List<string>();
        var liveIncidences = ReadLiveIncidences(issues);
        var reachedFromHyperedges = CheckHyperedgeChains(liveIncidences, issues);
        var reachedFromNodes = CheckNodeChains(liveIncidences, issues);

        foreach (var incidence in liveIncidences.Values)
        {
            if (!incidence.IsLive) continue;
            if (!reachedFromHyperedges.Contains(incidence.Id.Sequence))
                issues.Add($"Incidence {incidence.Id.Sequence} is unreachable from its hyperedge chain.");
            if (!reachedFromNodes.Contains(incidence.Id.Sequence))
                issues.Add($"Incidence {incidence.Id.Sequence} is unreachable from its node chain.");
        }

        return new ConsistencyReport(issues.Count == 0, issues);
    }

    // incidence は独立した可視性を持たないため、slot の生存と参照先 header の生存を
    // 分けて検査する。壊れた参照も後段の到達不能検査へ残すことで、一度の走査で根因と影響を示す。
    private Dictionary<long, IncidenceSnapshot> ReadLiveIncidences(List<string> issues)
    {
        var result = new Dictionary<long, IncidenceSnapshot>();
        for (long sequence = 0; sequence < _incidenceStore.SequenceHighWaterMark; sequence++)
        {
            using var handle = _incidenceStore.Read(new IncidenceId(sequence));
            if (!handle.InUse) continue;

            var incidence = new IncidenceSnapshot(
                handle.Id,
                handle.HyperedgeId,
                handle.NodeId,
                handle.RoleId,
                handle.NextInNode,
                handle.NextInHyperedge,
                IsLive: false);
            using var header = _hyperedgeStore.Read(incidence.HyperedgeId);
            bool hasOwner = incidence.HyperedgeId.IsValid
                && _hyperedgeStore.TryReadRawHeader(incidence.HyperedgeId.Sequence, out _);
            if (!hasOwner)
                issues.Add($"Incidence {sequence} references an invalid hyperedge.");
            else if (!header.InUse)
            {
                result[sequence] = incidence;
                continue;
            }

            incidence = incidence with { IsLive = true };
            result[sequence] = incidence;
            if (!incidence.NodeId.IsValid || !_nodeStore.Read(incidence.NodeId).InUse)
                issues.Add($"Incidence {sequence} references an invalid node.");
            if (!incidence.RoleId.IsValid)
                issues.Add($"Incidence {sequence} references an invalid role.");
        }
        return result;
    }

    private HashSet<long> CheckHyperedgeChains(
        IReadOnlyDictionary<long, IncidenceSnapshot> liveIncidences,
        List<string> issues)
    {
        var reached = new HashSet<long>();
        foreach (HyperedgeId hyperedgeId in _hyperedgeStore.Scan())
        {
            using var header = _hyperedgeStore.Read(hyperedgeId);
            var chain = new HashSet<long>();
            var members = new HashSet<(long Node, int Role)>();
            IncidenceId current = header.FirstIncidenceId;
            int arity = 0;

            while (current.IsValid)
            {
                if (!chain.Add(current.Sequence))
                {
                    issues.Add($"Hyperedge {hyperedgeId.Sequence} incidence chain contains a cycle.");
                    break;
                }
                if (!liveIncidences.TryGetValue(current.Sequence, out var incidence))
                {
                    issues.Add($"Hyperedge {hyperedgeId.Sequence} chain references an unused incidence {current.Sequence}.");
                    break;
                }

                reached.Add(current.Sequence);
                arity++;
                if (incidence.HyperedgeId.Sequence != hyperedgeId.Sequence)
                    issues.Add($"Incidence {current.Sequence} is linked from the wrong hyperedge chain.");
                if (!members.Add((incidence.NodeId.Sequence, incidence.RoleId.Value)))
                    issues.Add($"Hyperedge {hyperedgeId.Sequence} contains a duplicate role and node pair.");
                current = incidence.NextInHyperedge;
            }

            if (arity < 2)
                issues.Add($"Hyperedge {hyperedgeId.Sequence} has arity {arity}; at least two members are required.");
        }
        return reached;
    }

    private HashSet<long> CheckNodeChains(
        IReadOnlyDictionary<long, IncidenceSnapshot> liveIncidences,
        List<string> issues)
    {
        var reached = new HashSet<long>();
        foreach (NodeId nodeId in _nodeStore.Scan())
        {
            var chain = new HashSet<long>();
            IncidenceId current = _nodeIncidenceHeads.Get(nodeId);
            while (current.IsValid)
            {
                if (!chain.Add(current.Sequence))
                {
                    issues.Add($"Node {nodeId.Sequence} incidence chain contains a cycle.");
                    break;
                }
                if (!liveIncidences.TryGetValue(current.Sequence, out var incidence))
                {
                    issues.Add($"Node {nodeId.Sequence} chain references an unused incidence {current.Sequence}.");
                    break;
                }

                if (incidence.IsLive)
                {
                    reached.Add(current.Sequence);
                // incidence の node 参照は physical Sequence、node scan は logical full ID。
                // 診断の chain 整合性は物理 address を検査するため、Generation 込み equality を
                // 使うと正常な chain を別ノード扱いしてしまう。
                if (incidence.NodeId.Sequence != nodeId.Sequence)
                    issues.Add($"Incidence {current.Sequence} is linked from the wrong node chain.");
                }
                current = incidence.NextInNode;
            }
        }
        return reached;
    }

    private readonly record struct IncidenceSnapshot(
        IncidenceId Id,
        HyperedgeId HyperedgeId,
        NodeId NodeId,
        RoleId RoleId,
        IncidenceId NextInNode,
        IncidenceId NextInHyperedge,
        bool IsLive);

    /// <summary>
    /// 全 B+Tree 索引を走査し、解放済みノード ID を指す orphan エントリを検出する。
    /// の <c>LabelNodeIndex</c> も同じ live 判定で覆い、orphan 件数を返却する。
    /// </summary>
    public IndexConsistencyReport CheckIndexConsistency()
    {
        var (indexCount, entryCount, rawOrphans, labelOrphans) = ScanOrphans();
        var orphanEntries = ToPublicOrphans(rawOrphans);

        // index-orphan-count gauge は CheckIndexConsistency が呼ばれた時点の観測値を保持。
        // B+Tree orphan + LabelIndex orphan の合計。
        QuiverEventSource.Log.SetIndexOrphanCount(orphanEntries.Count + labelOrphans);

        return new IndexConsistencyReport(
            IndexCount: indexCount,
            EntryCount: entryCount,
            OrphanCount: orphanEntries.Count,
            Orphans: orphanEntries,
            LabelIndexOrphanCount: labelOrphans);
    }

    /// <summary>
    /// orphan を実削除する。<see cref="IndexRepairMode.DryRun"/> 時は
    /// 検出した orphan 一覧のみ返す。<see cref="IndexRepairMode.Apply"/> 時は
    /// <see cref="IndexManager.RemoveOrphans"/> で生キー削除し、<c>LabelNodeIndex</c> は
    /// orphan が 1 件でもあれば <c>Invalidate()</c> して次回 lookup での再構築に委ねる
    /// (in-memory index なので部分削除より rebuild の方が簡潔)。
    /// </summary>
    /// <remarks>
    /// 削除は索引に格納された raw な packed 値 (<see cref="EntityRef"/>) で行う必要がある
    /// (<c>DeleteRawEntry</c> は値の完全一致で消すため)。公開 <see cref="OrphanIndexEntry.EntityId"/> は
    /// unpacked な NodeId.Value なので、削除には内部 raw リストを使う。
    /// </remarks>
    public IndexRepairReport RepairIndexes(IndexRepairMode mode)
    {
        var (_, _, rawOrphans, labelOrphans) = ScanOrphans();
        var orphanEntries = ToPublicOrphans(rawOrphans);

        if (mode == IndexRepairMode.DryRun)
            return new IndexRepairReport(0, orphanEntries, false);

        int removed = 0;
        if (_indexManager != null && rawOrphans.Count > 0)
            removed = _indexManager.RemoveOrphans(rawOrphans);

        bool labelInvalidated = false;
        if (_labelIndex != null && labelOrphans > 0)
        {
            _labelIndex.Invalidate();
            labelInvalidated = true;
        }

        return new IndexRepairReport(removed, orphanEntries, labelInvalidated);
    }

    /// <summary>
    /// 全 B+Tree 索引を 1 回走査し、(走査索引数, 走査エントリ数, raw orphan 一覧, LabelIndex orphan 数)
    /// を返す。raw orphan の <c>Value</c> は索引に格納された packed 値 (削除に使う)。
    /// </summary>
    private (int IndexCount, long EntryCount, List<(string IndexName, byte[] RawKey, long Value)> Raw, long LabelOrphans) ScanOrphans()
    {
        var raw = new List<(string IndexName, byte[] RawKey, long Value)>();
        int indexCount = 0;
        long entryCount = 0;
        if (_indexManager != null)
            (indexCount, entryCount) = _indexManager.CollectOrphans(IsLiveNode, raw);
        return (indexCount, entryCount, raw, CountLabelIndexOrphans());
    }

    /// <summary>raw orphan (packed 値) を公開 <see cref="OrphanIndexEntry"/> (unpacked NodeId.Value) へ変換。</summary>
    private static List<OrphanIndexEntry> ToPublicOrphans(List<(string IndexName, byte[] RawKey, long Value)> raw)
    {
        var int64 = new Int64KeyCodec();
        var list = new List<OrphanIndexEntry>(raw.Count);
        foreach (var (name, key, value) in raw)
        {
            int sep = name.IndexOf(IndexManager.FtLaneSep);
            if (sep >= 0)
            {
                // 全文索引 lane。EntityId は value (tf/docLen) ではなく key 側に入っている
                // (postings=末尾8B の packed ref / norms=Int64 key の packed ref)。表示名は lane タグを外す。
                var lane = name[(sep + 1)..];
                long packed = lane == IndexManager.PostingsLaneTag
                    ? PostingsKey.DecodeEntityId(key)
                    : int64.Decode(key);
                list.Add(new OrphanIndexEntry(name[..sep], key, EntityRef.UnpackSequence(packed)));
            }
            else
            {
                list.Add(new OrphanIndexEntry(name, key, EntityRef.UnpackSequence(value)));
            }
        }
        return list;
    }

    private bool IsLiveNode(long packedValue)
    {
        // /: B+Tree 索引値は EntityRef でパック済み (Kind/Generation/Sequence)。
        // 索引は現状 Node 限定。Node 以外、物理的に解放済み (InUse=false)、または slot が
        // 再利用されて世代が食い違う (ABA) エントリは orphan とみなす。
        if (EntityRef.UnpackKind(packedValue) != EntityKind.Node) return false;
        long seq = EntityRef.UnpackSequence(packedValue);
        return _nodeStore.Read(new NodeId(seq)).InUse
            && _nodeStore.CurrentGeneration(seq) == EntityRef.UnpackGeneration(packedValue);
    }

    private long CountLabelIndexOrphans()
    {
        if (_labelIndex == null || !_labelIndex.IsBuilt) return 0;
        long count = 0;
        foreach (var (_, node) in _labelIndex.EnumerateEntries())
        {
            if (!_nodeStore.Read(node).InUse) count++;
        }
        return count;
    }
}
