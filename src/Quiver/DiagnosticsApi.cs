using Quiver.Core;
using Quiver.Core.Telemetry;
using Quiver.Index;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

internal sealed class DiagnosticsApi : IDiagnosticsApi
{
    private readonly INodeStore _nodeStore;
    private readonly IRelationshipStore _relStore;
    private readonly IGraphAccessMethods _access;
    // FT-22: orphan 検出は型を意識せずに全索引を走査する必要があるため
    // IIndexManager の non-generic 経路を直接持つ。
    private readonly IndexManager? _indexManager;
    private readonly LabelNodeIndex? _labelIndex;
    // FT-28: Adaptive checkpoint controller の現在 threshold と policy 切り替えを公開する経路。
    private readonly TransactionManager? _txManager;
    // FT-28: SetCheckpointPolicy(Adaptive, ...) で新規 controller を構築するための保存値。
    private readonly TimeSpan _adaptiveTargetRecoveryTime;
    private readonly long _adaptiveMinThresholdBytes;
    private readonly long _adaptiveMaxThresholdBytes;
    private readonly int _adaptiveSampleWindow;

    internal DiagnosticsApi(
        INodeStore nodeStore,
        IRelationshipStore relStore,
        IGraphAccessMethods access,
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
        PropertyCount: 0,
        DataFileSize: 0,
        WalFileSize: 0,
        BufferPoolHits: 0,
        BufferPoolMisses: 0,
        AdjacencyFallbackCount: _access.AdjacencyFallbackCount);

    public ConsistencyReport CheckConsistency() => new(true, []);

    /// <summary>
    /// FT-22: 全 B+Tree 索引を走査し、解放済みノード ID を指す orphan エントリを検出する。
    /// VEC-11 の <c>LabelNodeIndex</c> も同じ live 判定で覆い、orphan 件数を返却する。
    /// </summary>
    public IndexConsistencyReport CheckIndexConsistency()
    {
        var orphans = new List<(string IndexName, byte[] RawKey, long Value)>();
        int indexCount = 0;
        long entryCount = 0;
        if (_indexManager != null)
        {
            (indexCount, entryCount) = _indexManager.CollectOrphans(IsLiveNode, orphans);
        }

        long labelOrphans = CountLabelIndexOrphans();

        var orphanEntries = new List<OrphanIndexEntry>(orphans.Count);
        foreach (var (name, key, value) in orphans)
            orphanEntries.Add(new OrphanIndexEntry(name, key, value));

        // OB-2: index-orphan-count gauge は CheckIndexConsistency が呼ばれた時点の観測値を保持。
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
    /// FT-22: orphan を実削除する。<see cref="IndexRepairMode.DryRun"/> 時は
    /// 検出した orphan 一覧のみ返す。<see cref="IndexRepairMode.Apply"/> 時は
    /// <see cref="IndexManager.RemoveOrphans"/> で生キー削除し、<c>LabelNodeIndex</c> は
    /// orphan が 1 件でもあれば <c>Invalidate()</c> して次回 lookup での再構築に委ねる
    /// (in-memory index なので部分削除より rebuild の方が簡潔)。
    /// </summary>
    public IndexRepairReport RepairIndexes(IndexRepairMode mode)
    {
        var report = CheckIndexConsistency();
        if (mode == IndexRepairMode.DryRun)
            return new IndexRepairReport(0, report.Orphans, false);

        int removed = 0;
        if (_indexManager != null && report.Orphans.Count > 0)
        {
            var pairs = new List<(string, byte[], long)>(report.Orphans.Count);
            foreach (var o in report.Orphans)
                pairs.Add((o.IndexName, o.RawKey, o.EntityId));
            removed = _indexManager.RemoveOrphans(pairs);
        }

        bool labelInvalidated = false;
        if (_labelIndex != null && report.LabelIndexOrphanCount > 0)
        {
            _labelIndex.Invalidate();
            labelInvalidated = true;
        }

        return new IndexRepairReport(removed, report.Orphans, labelInvalidated);
    }

    private bool IsLiveNode(long entityIdValue)
    {
        // FT-22: B+Tree 索引は IndexInsert 経路でしか書かれず、現状は NodeId 限定。
        // RelationshipId に拡張された場合は EntityKind を載せる必要があるが、いまは Node のみ。
        var nodeId = new NodeId(entityIdValue);
        return _nodeStore.Read(nodeId).InUse;
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
