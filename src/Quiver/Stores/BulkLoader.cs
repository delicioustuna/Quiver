using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// 初期データインポート用の append-only bulk loader。
/// WAL とレコード単位のメタフラッシュをバイパスし、<see cref="Commit"/> で一括フラッシュする。
/// 提供される ID はすべて新規 (既存レコードと衝突しない) であることを前提とする。
/// Self-loop はサポートするが、TgtPrev/TgtNext は SrcPrev/SrcNext をミラーする。
/// </summary>
public sealed class BulkLoader : IDisposable
{
    private readonly VersionedNodeStore _nodeStore;
    private readonly VersionedRelationshipStore _relStore;
    private readonly PropertyStore _propStore;
    // 隣接ビューは graph.quiver 内テナントへ構築する (null = 構築しない)。
    private readonly Quiver.Storage.SingleFileContainer? _container;

    private readonly List<PendingNode> _nodes = new();
    private readonly List<PendingRel> _rels = new();
    private readonly Dictionary<long, List<PendingProp>> _propsByNode = new();
    // リレーションシップごとの V2 payload lane 用 raw 値を収集する。
    // (RelationshipId, PropertyKeyId) でキーイングしているため同一ローダが複数の payload キー
    // 候補を受けられるが、実際に inline されるのは WithPayloadLane で指定されたもののみ。
    private readonly Dictionary<(long RelId, int KeyId), long> _relPayloads = new();
    private PayloadLaneSpec? _payloadSpec;
    private bool _committed;

    private record struct PendingNode(long Id, int LabelId);
    private record struct PendingRel(long Id, long Src, long Tgt, int TypeId);
    private readonly record struct PendingProp(int KeyId, PropertyValueType Type, long Scalar, byte[]? Data);

    internal BulkLoader(VersionedNodeStore nodeStore, VersionedRelationshipStore relStore, PropertyStore propStore,
        Quiver.Storage.SingleFileContainer? container = null)
    {
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _container = container;
    }

    /// <summary>ノードを追加する (ラベル付き)。</summary>
    public void AppendNode(NodeId id, LabelId label)
    {
        ThrowIfCommitted();
        // 物理 slot は Sequence (利用側が gen 付き id を渡しても正しく正規化)。
        _nodes.Add(new PendingNode(id.Sequence, label.Value));
    }

    /// <summary>リレーションシップを追加する (順不同で可)。</summary>
    public void AppendRelationship(RelationshipId id, NodeId from, NodeId to, RelationshipTypeId type)
    {
        ThrowIfCommitted();
        _rels.Add(new PendingRel(id.Sequence, from.Sequence, to.Sequence, type.Value));
    }

    /// <summary>ノードにプロパティを追加する。</summary>
    public void AppendProperty(NodeId nodeId, PropertyKeyId key, in PropertyValue value)
    {
        ThrowIfCommitted();
        byte[]? data = null;
        if (value.Type is PropertyValueType.String)
            data = value.Utf8StringValue.ToArray();
        else if (value.Type is PropertyValueType.Bytes)
            data = value.BytesValue.ToArray();

        if (!_propsByNode.TryGetValue(nodeId.Sequence, out var props))
            _propsByNode[nodeId.Sequence] = props = new();
        props.Add(new PendingProp(key.Value, value.Type, value.Int64Value, data));
    }

    /// <summary>
    /// inline payload lane を設定し、<see cref="Commit"/> で指定したリレーションシッププロパティを
    /// エッジエントリ毎に inline 格納した <c>AdjacencyBlockStoreV2</c> を構築させる
    /// 。以後の <see cref="AppendRelationshipPayload"/> で lane を埋め、
    /// 値の無いエッジには <c>spec.DefaultRaw</c> が入る。
    /// </summary>
    public void WithPayloadLane(PayloadLaneSpec spec)
    {
        ThrowIfCommitted();
        if (spec.Kind == PayloadKind.None)
            throw new ArgumentException("PayloadLaneSpec.Kind must be Int64 or Double.", nameof(spec));
        _payloadSpec = spec;
    }

    /// <summary>
    /// リレーションシップの inline payload 値を記録する。<see cref="WithPayloadLane"/> の spec と
    /// キーが一致する値のみが V2 ビューに inline され、他キーは破棄される。生の long は lane の
    /// 種別に応じて Int64 値または <c>BitConverter.DoubleToInt64Bits(d)</c>。
    /// </summary>
    public void AppendRelationshipPayload(RelationshipId relId, PropertyKeyId key, long rawValue)
    {
        ThrowIfCommitted();
        _relPayloads[(relId.Sequence, key.Value)] = rawValue;
    }

    /// <summary>溜めたノード / リレーションシップ / プロパティを 1 回のフラッシュで書き出し確定する。</summary>
    public void Commit()
    {
        ThrowIfCommitted();
        _committed = true;

        CommitNodes();
        CommitRelationships();
        CommitProperties();
        if (_container != null)
            BuildAdjacencyIndex(_container);
    }

    /// <summary>ローダを破棄する (現状は no-op)。</summary>
    public void Dispose() { }

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

    // 単一パス dense-array ポインタ計算。従来の 2 パス
    // Dictionary<long, List<(long, bool)>> + Dictionary<long, (long, long, long, long)>
    // 方式 (10M エッジで ~700+ MB 割り当て) を置き換える。アルゴリズム:
    //
    //   RelId 昇順で各 rel r について、各接触ノードのチェーンは src 側と tgt 側の rel を
    //   交互配置する。ノードごとに直近の rel と側を追跡し、同一ノードに新しい rel が来たら
    //   (1) Next フィールドを前の末尾に向け、(2) 前の末尾の Prev フィールドを適切な側で
    //   パッチする。各ノードの FirstRelId は最終的な lastByNode[node] (最大 RelId)。
    //
    // Self-loop: src 側のみをチェーンに記録する。書き込み時 tgt 側のフィールドは src を
    // ミラーする — 従来の実装と同じセマンティクス。
    private void CommitRelationships()
    {
        if (_rels.Count == 0)
        {
            _relStore.BulkSetHeaders(0, 0);
            return;
        }

        long relHwm = 0, nodeHwm = 0;
        foreach (var r in _rels)
        {
            if (r.Id  >= relHwm)  relHwm  = r.Id  + 1;
            if (r.Src >= nodeHwm) nodeHwm = r.Src + 1;
            if (r.Tgt >= nodeHwm) nodeHwm = r.Tgt + 1;
        }

        // チェーンポインタを正しい方向にパッチするため、RelId 昇順で処理する必要がある。
        // 呼び出し側は通常シーケンシャルに append するのでソートはほぼ no-op だが、
        // 防御的にソートする。
        _rels.Sort((a, b) => a.Id.CompareTo(b.Id));

        var srcPrev = new long[relHwm];
        var srcNext = new long[relHwm];
        var tgtPrev = new long[relHwm];
        var tgtNext = new long[relHwm];
        Array.Fill(srcPrev, -1L);
        Array.Fill(srcNext, -1L);
        Array.Fill(tgtPrev, -1L);
        Array.Fill(tgtNext, -1L);

        var lastByNode = new long[nodeHwm];
        var lastSide   = new byte[nodeHwm]; // 0 = src at this node, 1 = tgt
        Array.Fill(lastByNode, -1L);

        foreach (var r in _rels)
        {
            long prev = lastByNode[r.Src];
            if (prev >= 0)
            {
                if (lastSide[r.Src] == 0) srcPrev[prev] = r.Id;
                else                      tgtPrev[prev] = r.Id;
            }
            srcNext[r.Id] = prev;
            lastByNode[r.Src] = r.Id;
            lastSide[r.Src] = 0;

            if (r.Tgt != r.Src)
            {
                long prevT = lastByNode[r.Tgt];
                if (prevT >= 0)
                {
                    if (lastSide[r.Tgt] == 0) srcPrev[prevT] = r.Id;
                    else                      tgtPrev[prevT] = r.Id;
                }
                tgtNext[r.Id] = prevT;
                lastByNode[r.Tgt] = r.Id;
                lastSide[r.Tgt] = 1;
            }
        }

        long hwm = 0;
        foreach (var r in _rels)
        {
            long sp = srcPrev[r.Id], sn = srcNext[r.Id];
            long tp, tn;
            if (r.Src == r.Tgt) { tp = sp; tn = sn; }
            else                { tp = tgtPrev[r.Id]; tn = tgtNext[r.Id]; }

            _relStore.BulkWrite(r.Id, r.Src, r.Tgt, r.TypeId, sp, sn, tp, tn);
            if (r.Id >= hwm) hwm = r.Id + 1;
        }
        _relStore.BulkSetHeaders(hwm, _rels.Count);

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
            // tail-to-head でチェーンを構築する。最後に書き込まれたプロパティが head になる。
            long nextPropId = -1L;
            foreach (var prop in props)
            {
                var propId = _propStore.BulkCreate(prop.KeyId, prop.Type, prop.Scalar, prop.Data, nextPropId);
                nextPropId = propId.Sequence; // Int48 NextPropId は Sequence
            }
            _nodeStore.BulkUpdateFirstProp(nodeId, nextPropId);
        }
        _propStore.BulkFlushMeta();
    }

    private void BuildAdjacencyIndex(Quiver.Storage.SingleFileContainer container)
    {
        long nodeHwm = _nodes.Count > 0 ? _nodes.Max(n => n.Id) + 1 : 0L;
        long relHwm = _rels.Count > 0 ? _rels.Max(r => r.Id) + 1 : 0L;
        var relData = _rels.Select(r => (r.Id, r.Src, r.Tgt, r.TypeId)).ToList();

        Dictionary<long, long>? weights = null;
        if (_payloadSpec is { } spec)
        {
            // V2 ビルド。設定されたキーに payload をフィルタする。raw 値はそのまま渡される
            // (double<->long の再解釈は AppendRelationshipPayload 経由で呼び出し側の責任)。
            weights = new Dictionary<long, long>(_relPayloads.Count);
            foreach (var ((relId, keyId), raw) in _relPayloads)
            {
                if (keyId == spec.PropertyKeyId)
                    weights[relId] = raw;
            }
        }

        // relHwm を base watermark として記録し、post-bulk-load の delta
        // (id >= relHwm) を読み取り時に base ビューへ二重計上せず merge できるようにする。
        AdjacencyContainer.Build(container, relData, nodeHwm, relHwm, _payloadSpec, weights);
    }

    private void ThrowIfCommitted()
    {
        if (_committed) throw new InvalidOperationException("BulkLoader has already been committed.");
    }

}
