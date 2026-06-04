using System.Buffers.Binary;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// PW-9: 1000 万エッジ超の取り込みを想定した <see cref="BulkLoader"/> のストリーミング版。
/// <c>AppendRelationship</c> 中はリレーションシップレコードを <c>List&lt;PendingRel&gt;</c> に溜めず、
/// 一時バイナリファイルにストリーミング書き出しする。<see cref="Commit"/> で一時ファイルを 2 回読み出す —
/// 1 回目で双方向リンクチェーンのポインタを dense <c>long[]</c> 配列 (maxRelId+1 / maxNodeId+1 サイズ) に
/// 計算し、2 回目で <c>RelationshipStore.BulkWrite</c> を RelId 昇順で発行する。
///
/// 事前条件: <see cref="AppendRelationship"/> は <see cref="RelationshipId"/> が厳密に増加する順序で呼ぶ必要がある。
/// 典型的なバルクインポートでは順次 ID が割り当てられるためこの前提が成り立ち、ポインタ計算パスで
/// 別途ソート工程を省略できる。
///
/// 1000 万エッジ時のメモリフットプリント (adj 構築なし):
///   - ディスク上の一時ファイル:                          280 MB (28 B/rel)
///   - ポインタ配列 (4 × long[maxRelId+1]):             約 320 MB
///   - lastByNode / lastSide (ノード毎 long[] + byte[]): 100 万ノードで約 9 MB
///   合計ヒープ: 約 330 MB (旧 <c>BulkLoader</c> 約 1.7 GB と比較)。
///
/// ノードとプロパティは引き続きヒープ上 (一般的な負荷では小さい)。隣接インデックス構築は
/// 一時ファイルをコンパクトリストとして 1 回再読み込みする — これが唯一エッジ数に対して
/// 線形にヒープが伸びる経路。1000 万件超の取り込み計画がある呼び出し側は
/// <c>buildAdjacencyIndex: false</c> を選び、必要なら後段で <c>GraphDatabase.CompactAdjacency()</c> を
/// 呼ぶことを推奨する。
/// </summary>
public sealed class StreamingBulkLoader : IDisposable
{
    private readonly NodeStore _nodeStore;
    private readonly RelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    // ARCH-4 増分6: 隣接ビューは graph.quiver 内テナントへ構築する (null = 構築しない)。
    private readonly Quiver.Storage.SingleFileContainer? _container;

    private const int RelRecordSize = 28; // Id(8) + Src(8) + Tgt(8) + TypeId(4)

    private readonly string _tempPath;
    private readonly FileStream _relTemp;
    private readonly byte[] _writeBuf = new byte[RelRecordSize];

    private long _relCount;
    private long _maxRelId = -1;
    private long _maxNodeId = -1;

    private readonly List<PendingNode> _nodes = new();
    private readonly Dictionary<long, List<PendingProp>> _propsByNode = new();
    private readonly Dictionary<(long RelId, int KeyId), long> _relPayloads = new();
    private PayloadLaneSpec? _payloadSpec;
    private bool _committed;
    private bool _disposed;

    private record struct PendingNode(long Id, int LabelId);
    private readonly record struct PendingProp(int KeyId, PropertyValueType Type, long Scalar, byte[]? Data);

    internal StreamingBulkLoader(
        NodeStore nodeStore, RelationshipStore relStore, PropertyStore propStore,
        Quiver.Storage.SingleFileContainer? container = null)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _container = container;

        _tempPath = Path.Combine(
            Path.GetTempPath(),
            "quiver_streaming_bulk_" + Guid.NewGuid().ToString("N")[..16] + ".rels");
        _relTemp = new FileStream(
            _tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.DeleteOnClose);
    }

    public void AppendNode(NodeId id, LabelId label)
    {
        ThrowIfCommitted();
        // ARCH-5b: 物理 slot は Sequence (利用側が gen 付き id を渡しても正しく正規化)。
        _nodes.Add(new PendingNode(id.Sequence, label.Value));
        if (id.Sequence > _maxNodeId) _maxNodeId = id.Sequence;
    }

    public void AppendRelationship(RelationshipId id, NodeId from, NodeId to, RelationshipTypeId type)
    {
        ThrowIfCommitted();
        if (id.Sequence <= _maxRelId)
            throw new InvalidOperationException(
                $"StreamingBulkLoader requires AppendRelationship in strictly increasing " +
                $"RelationshipId order (got {id.Sequence}, last was {_maxRelId}). " +
                $"Use BulkLoader for unordered input.");

        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(0),  id.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(8),  from.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(16), to.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(_writeBuf.AsSpan(24), type.Value);
        _relTemp.Write(_writeBuf, 0, RelRecordSize);

        _relCount++;
        _maxRelId = id.Sequence;
        if (from.Sequence > _maxNodeId) _maxNodeId = from.Sequence;
        if (to.Sequence   > _maxNodeId) _maxNodeId = to.Sequence;
    }

    public void AppendProperty(NodeId nodeId, PropertyKeyId key, in PropertyValue value)
    {
        ThrowIfCommitted();
        byte[]? data = null;
        if (value.Type is PropertyValueType.String)
            data = value.Utf8StringValue.ToArray();
        else if (value.Type is PropertyValueType.Bytes)
            data = value.BytesValue.ToArray();

        if (!_propsByNode.TryGetValue(nodeId.Sequence, out var props)) // ARCH-5b: key は Sequence
            _propsByNode[nodeId.Sequence] = props = new();
        props.Add(new PendingProp(key.Value, value.Type, value.Int64Value, data));
    }

    public void WithPayloadLane(PayloadLaneSpec spec)
    {
        ThrowIfCommitted();
        if (spec.Kind == PayloadKind.None)
            throw new ArgumentException("PayloadLaneSpec.Kind must be Int64 or Double.", nameof(spec));
        _payloadSpec = spec;
    }

    public void AppendRelationshipPayload(RelationshipId relId, PropertyKeyId key, long rawValue)
    {
        ThrowIfCommitted();
        _relPayloads[(relId.Sequence, key.Value)] = rawValue; // ARCH-5b: rel key は Sequence
    }

    public void Commit()
    {
        ThrowIfCommitted();
        _committed = true;

        _relTemp.Flush();

        CommitNodes();
        CommitRelationshipsStreaming();
        CommitProperties();
        if (_container != null)
            BuildAdjacencyIndexStreaming(_container);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _relTemp.Dispose(); } // FileOptions.DeleteOnClose handles cleanup
        catch { /* swallow — temp file cleanup is best-effort */ }
    }

    // -----------------------------------------------------------------------

    private void CommitNodes()
    {
        long hwm = 0;
        foreach (var node in _nodes)
        {
            _nodeStore.BulkWrite(node.Id, node.LabelId);
            if (node.Id >= hwm) hwm = node.Id + 1;
        }
        _nodeStore.BulkSetHeaders(hwm, _nodes.Count);
    }

    private void CommitRelationshipsStreaming()
    {
        if (_relCount == 0)
        {
            _relStore.BulkSetHeaders(0, 0);
            return;
        }

        long relHwm = _maxRelId + 1;
        long nodeHwm = _maxNodeId + 1;

        var srcPrev = new long[relHwm]; Array.Fill(srcPrev, -1L);
        var srcNext = new long[relHwm]; Array.Fill(srcNext, -1L);
        var tgtPrev = new long[relHwm]; Array.Fill(tgtPrev, -1L);
        var tgtNext = new long[relHwm]; Array.Fill(tgtNext, -1L);

        var lastByNode = new long[nodeHwm]; Array.Fill(lastByNode, -1L);
        var lastSide   = new byte[nodeHwm]; // 0 = was src at this node, 1 = was tgt

        // Pass 1: compute pointers via single forward scan. Algorithm matches the in-memory
        // BulkLoader (see BulkLoader.CommitRelationships) — chain at every node interleaves
        // rels regardless of side; the field that gets the pointer depends on which side
        // the rel sits on for that particular node.
        var buf = new byte[RelRecordSize];
        _relTemp.Position = 0;
        for (long i = 0; i < _relCount; i++)
        {
            _relTemp.ReadExactly(buf, 0, RelRecordSize);
            long id  = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));

            long prev = lastByNode[src];
            if (prev >= 0)
            {
                if (lastSide[src] == 0) srcPrev[prev] = id;
                else                    tgtPrev[prev] = id;
            }
            srcNext[id] = prev;
            lastByNode[src] = id;
            lastSide[src] = 0;

            if (tgt != src)
            {
                long prevT = lastByNode[tgt];
                if (prevT >= 0)
                {
                    if (lastSide[tgt] == 0) srcPrev[prevT] = id;
                    else                    tgtPrev[prevT] = id;
                }
                tgtNext[id] = prevT;
                lastByNode[tgt] = id;
                lastSide[tgt] = 1;
            }
        }

        // Pass 2: stream rels again, applying pointers via dense lookup.
        long hwm = 0;
        _relTemp.Position = 0;
        for (long i = 0; i < _relCount; i++)
        {
            _relTemp.ReadExactly(buf, 0, RelRecordSize);
            long id     = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));
            int  typeId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24));

            long sp = srcPrev[id], sn = srcNext[id];
            long tp, tn;
            if (src == tgt) { tp = sp; tn = sn; }
            else            { tp = tgtPrev[id]; tn = tgtNext[id]; }

            _relStore.BulkWrite(id, src, tgt, typeId, sp, sn, tp, tn);
            if (id >= hwm) hwm = id + 1;
        }
        _relStore.BulkSetHeaders(hwm, _relCount);

        for (long n = 0; n < nodeHwm; n++)
        {
            long head = lastByNode[n];
            if (head >= 0)
                _nodeStore.UpdateFirstRelId(new NodeId(n), new RelationshipId(head));
        }
    }

    private void CommitProperties()
    {
        foreach (var (nodeId, props) in _propsByNode)
        {
            long nextPropId = -1L;
            foreach (var prop in props)
            {
                var propId = _propStore.BulkCreate(prop.KeyId, prop.Type, prop.Scalar, prop.Data, nextPropId);
                nextPropId = propId.Sequence; // ARCH-5b: Int48 NextPropId は Sequence
            }
            _nodeStore.BulkUpdateFirstProp(nodeId, nextPropId);
        }
        _propStore.BulkFlushMeta();
    }

    private void BuildAdjacencyIndexStreaming(Quiver.Storage.SingleFileContainer container)
    {
        // Materialize rels into a compact list for AdjacencyContainer.Build.
        // Full streaming adj-build (chunk-sort by src/tgt) is a future task — see PW-9 notes.
        var relData = new List<(long Id, long Src, long Tgt, int TypeId)>((int)Math.Min(_relCount, int.MaxValue));
        var buf = new byte[RelRecordSize];
        _relTemp.Position = 0;
        for (long i = 0; i < _relCount; i++)
        {
            _relTemp.ReadExactly(buf, 0, RelRecordSize);
            long id     = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));
            int  typeId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24));
            relData.Add((id, src, tgt, typeId));
        }

        long nodeHwm = _maxNodeId + 1;
        long relHwm = _maxRelId + 1;

        Dictionary<long, long>? weights = null;
        if (_payloadSpec is { } spec)
        {
            weights = new Dictionary<long, long>(_relPayloads.Count);
            foreach (var ((relId, keyId), raw) in _relPayloads)
                if (keyId == spec.PropertyKeyId)
                    weights[relId] = raw;
        }
        AdjacencyContainer.Build(container, relData, nodeHwm, relHwm, _payloadSpec, weights);
    }

    private void ThrowIfCommitted()
    {
        if (_committed)
            throw new InvalidOperationException("StreamingBulkLoader has already been committed.");
    }
}
