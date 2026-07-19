using Quiver.Core;
using Quiver.Telemetry;
using Quiver.Index;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

internal sealed class DiagnosticsApi : IDiagnosticsApi
{
    private readonly IVertexStore _vertexStore;
    private readonly IEdgeStore _edgeStore;
    private readonly INexusStore _nexusStore;
    private readonly IPropertyStore _propertyStore;
    private readonly IIncidenceStore _incidenceStore;
    private readonly IVertexIncidenceHeadStore _vertexIncidenceHeads;
    private readonly IGraphAccessMethods _access;
    // orphan 検出は型を意識せずに全索引を走査する必要があるため
    // IIndexManager の non-generic 経路を直接持つ。
    private readonly IndexManager? _indexManager;
    private readonly LabelVertexIndex? _labelIndex;
    // Adaptive checkpoint controller の現在 threshold と policy 切り替えを公開する経路。
    private readonly TransactionManager? _txManager;
    // SetCheckpointPolicy(Adaptive, ...) で新規 controller を構築するための保存値。
    private readonly TimeSpan _adaptiveTargetRecoveryTime;
    private readonly long _adaptiveMinThresholdBytes;
    private readonly long _adaptiveMaxThresholdBytes;
    private readonly int _adaptiveSampleWindow;

    internal DiagnosticsApi(
        IVertexStore vertexStore,
        IEdgeStore edgeStore,
        IGraphAccessMethods access,
        IPropertyStore propertyStore,
        INexusStore? nexusStore = null,
        IIncidenceStore? incidenceStore = null,
        IVertexIncidenceHeadStore? vertexIncidenceHeads = null,
        IndexManager? indexManager = null,
        LabelVertexIndex? labelIndex = null,
        TransactionManager? txManager = null,
        TimeSpan? adaptiveTargetRecoveryTime = null,
        long adaptiveMinThresholdBytes = 4L * 1024 * 1024,
        long adaptiveMaxThresholdBytes = 1024L * 1024 * 1024,
        int adaptiveSampleWindow = 1000)
    {
        _vertexStore = vertexStore;
        _edgeStore = edgeStore;
        _propertyStore = propertyStore;
        _nexusStore = nexusStore ?? NullNexusStore.Instance;
        _incidenceStore = incidenceStore ?? NullIncidenceStore.Instance;
        _vertexIncidenceHeads = vertexIncidenceHeads ?? NullVertexIncidenceHeadStore.Instance;
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

    public SnapshotRuntimeDiagnostics GetSnapshotDiagnostics()
    {
        SnapshotDiagnostics diagnostics =
            _txManager?.Snapshots.Diagnostics ?? default;
        return new(
            diagnostics.ActiveCount,
            diagnostics.OldestAge,
            diagnostics.OldestStartLocation,
            diagnostics.OldestCommittedHighWater);
    }

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
        VertexCount: _vertexStore.InUseCount,
        EdgeCount: _edgeStore.InUseCount,
        NexusCount: _nexusStore.InUseCount,
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
        var reachedFromNexuses = CheckNexusChains(liveIncidences, issues);
        var reachedFromVertices = CheckVertexChains(liveIncidences, issues);

        foreach (var incidence in liveIncidences.Values)
        {
            if (!incidence.IsLive) continue;
            if (!reachedFromNexuses.Contains(incidence.Id.Sequence))
                issues.Add($"Incidence {incidence.Id.Sequence} is unreachable from its nexus chain.");
            if (!reachedFromVertices.Contains(incidence.Id.Sequence))
                issues.Add($"Incidence {incidence.Id.Sequence} is unreachable from its vertex chain.");
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
                handle.NexusId,
                handle.VertexId,
                handle.RoleId,
                handle.NextInVertex,
                handle.NextInNexus,
                IsLive: false);
            using var header = _nexusStore.Read(incidence.NexusId);
            bool hasOwner = incidence.NexusId.IsValid
                && _nexusStore.TryReadRawHeader(incidence.NexusId.Sequence, out _);
            if (!hasOwner)
                issues.Add($"Incidence {sequence} references an invalid nexus.");
            else if (!header.InUse)
            {
                result[sequence] = incidence;
                continue;
            }

            incidence = incidence with { IsLive = true };
            result[sequence] = incidence;
            if (!incidence.VertexId.IsValid || !_vertexStore.Read(incidence.VertexId).InUse)
                issues.Add($"Incidence {sequence} references an invalid vertex.");
            if (!incidence.RoleId.IsValid)
                issues.Add($"Incidence {sequence} references an invalid role.");
        }
        return result;
    }

    private HashSet<long> CheckNexusChains(
        IReadOnlyDictionary<long, IncidenceSnapshot> liveIncidences,
        List<string> issues)
    {
        var reached = new HashSet<long>();
        foreach (NexusId nexusId in _nexusStore.Scan())
        {
            using var header = _nexusStore.Read(nexusId);
            var chain = new HashSet<long>();
            var members = new HashSet<(long Vertex, int Role)>();
            IncidenceId current = header.FirstIncidenceId;
            int arity = 0;

            while (current.IsValid)
            {
                if (!chain.Add(current.Sequence))
                {
                    issues.Add($"Nexus {nexusId.Sequence} incidence chain contains a cycle.");
                    break;
                }
                if (!liveIncidences.TryGetValue(current.Sequence, out var incidence))
                {
                    issues.Add($"Nexus {nexusId.Sequence} chain references an unused incidence {current.Sequence}.");
                    break;
                }

                reached.Add(current.Sequence);
                arity++;
                if (incidence.NexusId.Sequence != nexusId.Sequence)
                    issues.Add($"Incidence {current.Sequence} is linked from the wrong nexus chain.");
                if (!members.Add((incidence.VertexId.Sequence, incidence.RoleId.Value)))
                    issues.Add($"Nexus {nexusId.Sequence} contains a duplicate role and vertex pair.");
                current = incidence.NextInNexus;
            }

            if (arity < 2)
                issues.Add($"Nexus {nexusId.Sequence} has arity {arity}; at least two members are required.");
        }
        return reached;
    }

    private HashSet<long> CheckVertexChains(
        IReadOnlyDictionary<long, IncidenceSnapshot> liveIncidences,
        List<string> issues)
    {
        var reached = new HashSet<long>();
        foreach (VertexId vertexId in _vertexStore.Scan())
        {
            var chain = new HashSet<long>();
            IncidenceId current = _vertexIncidenceHeads.Get(vertexId);
            while (current.IsValid)
            {
                if (!chain.Add(current.Sequence))
                {
                    issues.Add($"Vertex {vertexId.Sequence} incidence chain contains a cycle.");
                    break;
                }
                if (!liveIncidences.TryGetValue(current.Sequence, out var incidence))
                {
                    issues.Add($"Vertex {vertexId.Sequence} chain references an unused incidence {current.Sequence}.");
                    break;
                }

                if (incidence.IsLive)
                {
                    reached.Add(current.Sequence);
                // incidence の vertex 参照は physical Sequence、vertex scan は logical full ID。
                // 診断の chain 整合性は物理 address を検査するため、Generation 込み equality を
                // 使うと正常な chain を別Vertex扱いしてしまう。
                if (incidence.VertexId.Sequence != vertexId.Sequence)
                    issues.Add($"Incidence {current.Sequence} is linked from the wrong vertex chain.");
                }
                current = incidence.NextInVertex;
            }
        }
        return reached;
    }

    private readonly record struct IncidenceSnapshot(
        IncidenceId Id,
        NexusId NexusId,
        VertexId VertexId,
        RoleId RoleId,
        IncidenceId NextInVertex,
        IncidenceId NextInNexus,
        bool IsLive);

    /// <summary>
    /// 全 B+Tree 索引を走査し、解放済みVertex ID を指す orphan エントリを検出する。
    /// の <c>LabelVertexIndex</c> も同じ live 判定で覆い、orphan 件数を返却する。
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
    /// <see cref="IndexManager.RemoveOrphans"/> で生キー削除し、<c>LabelVertexIndex</c> は
    /// orphan が 1 件でもあれば <c>Invalidate()</c> して次回 lookup での再構築に委ねる
    /// (in-memory index なので部分削除より rebuild の方が簡潔)。
    /// </summary>
    /// <remarks>
    /// 削除は索引に格納された raw な packed 値 (<see cref="EntityRef"/>) で行う必要がある
    /// (<c>DeleteRawEntry</c> は値の完全一致で消すため)。公開 <see cref="OrphanIndexEntry.EntityId"/> は
    /// unpacked な VertexId.Value なので、削除には内部 raw リストを使う。
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
            (indexCount, entryCount) = _indexManager.CollectOrphans(
                IsLiveIndexReference,
                IsLiveEntityReference,
                raw);
        return (indexCount, entryCount, raw, CountLabelIndexOrphans());
    }

    /// <summary>raw orphan (packed 値) を公開 <see cref="OrphanIndexEntry"/> (unpacked VertexId.Value) へ変換。</summary>
    private List<OrphanIndexEntry> ToPublicOrphans(List<(string IndexName, byte[] RawKey, long Value)> raw)
    {
        var list = new List<OrphanIndexEntry>(raw.Count);
        foreach (var (name, key, value) in raw)
        {
            PropertyVersionRecord property = _propertyStore.Read(
                new PropertyVersionRef(value));
            long ownerSequence = property.Address.Owner.IsValid
                ? property.Address.Owner.Sequence
                : new PropertyVersionRef(value).Sequence;
            list.Add(new OrphanIndexEntry(name, key, ownerSequence));
        }
        return list;
    }

    private bool IsLiveIndexReference(long value)
    {
        PropertyVersionRecord property = _propertyStore.Read(
            new PropertyVersionRef(value));
        if (!property.InUse)
            return false;

        EntityRef owner = property.Address.Owner;
        return owner.Kind switch
        {
            EntityKind.Vertex => _vertexStore.Read(
                new VertexId(owner.Value)).InUse,
            EntityKind.Edge => _edgeStore.Read(
                new EdgeId(owner.Value)).InUse,
            EntityKind.Nexus => _nexusStore.Read(
                new NexusId(owner.Value)).InUse,
            _ => false,
        };
    }

    private bool IsLiveEntityReference(long value)
    {
        EntityKind kind = EntityRef.UnpackKind(value);
        EntityRef entity = EntityRef.Create(
            kind,
            EntityRef.UnpackSequence(value),
            EntityRef.UnpackGeneration(value));
        return entity.Kind switch
        {
            EntityKind.Vertex => _vertexStore.Read(
                new VertexId(entity.Value)).InUse,
            EntityKind.Edge => _edgeStore.Read(
                new EdgeId(entity.Value)).InUse,
            EntityKind.Nexus => _nexusStore.Read(
                new NexusId(entity.Value)).InUse,
            _ => false,
        };
    }

    private long CountLabelIndexOrphans()
    {
        if (_labelIndex == null || !_labelIndex.IsBuilt) return 0;
        long count = 0;
        foreach (var (_, vertex) in _labelIndex.EnumerateEntries())
        {
            if (!_vertexStore.Read(vertex).InUse) count++;
        }
        return count;
    }
}
