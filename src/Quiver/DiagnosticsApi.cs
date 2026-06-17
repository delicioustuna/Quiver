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
    /// 全 B+Tree 索引を走査し、解放済みノード ID を指す orphan エントリを検出する。
    /// の <c>LabelNodeIndex</c> も同じ live 判定で覆い、orphan 件数を返却する。
    /// </summary>
    public IndexConsistencyReport CheckIndexConsistency()
    {
        var (indexCount, entryCount, rawOrphans, labelOrphans) = ScanOrphans();
        var orphanEntries = ToPublicOrphans(rawOrphans);

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
                // FTS-2: 全文索引 lane。EntityId は value (tf/docLen) ではなく key 側に入っている
                // (postings=末尾8B の packed ref / norms=Int64 key の packed ref)。表示名は lane タグを外す。
                var lane = name[(sep + 1)..];
                long packed = lane == IndexManager.PostingsLaneTag
                    ? PostingsKey.DecodeEntityId(key)
                    : int64.Decode(key);
                list.Add(new OrphanIndexEntry(name[..sep], key, EntityRef.Sequence(packed)));
            }
            else
            {
                list.Add(new OrphanIndexEntry(name, key, EntityRef.Sequence(value)));
            }
        }
        return list;
    }

    private bool IsLiveNode(long packedValue)
    {
        // FT-22 / ARCH-3: B+Tree 索引値は EntityRef でパック済み (Kind/Generation/Sequence)。
        // 索引は現状 Node 限定。Node 以外、物理的に解放済み (InUse=false)、または slot が
        // 再利用されて世代が食い違う (ABA) エントリは orphan とみなす。
        if (EntityRef.UnpackKind(packedValue) != EntityKind.Node) return false;
        long seq = EntityRef.Sequence(packedValue);
        return _nodeStore.Read(new NodeId(seq)).InUse
            && _nodeStore.CurrentGeneration(seq) == EntityRef.Generation(packedValue);
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
