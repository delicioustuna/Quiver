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
    private readonly VersionedVertexStore _vertexStore;
    private readonly VersionedEdgeStore _edgeStore;
    private readonly PropertyVersionStore _propStore;
    // 隣接ビューは graph.quiver 内テナントへ構築する (null = 構築しない)。
    private readonly Quiver.Storage.SingleFileContainer? _container;

    private readonly List<PendingVertex> _vertices = new();
    private readonly List<PendingEdge> _edges = new();
    private readonly Dictionary<long, List<PendingProp>> _propsByVertex = new();
    // Edgeごとの payload lane 用 raw 値を収集する。
    // (EdgeId, PropertyKeyId) でキーイングしているため同一ローダが複数の payload キー
    // 候補を受けられるが、実際に inline されるのは WithPayloadLane で指定されたもののみ。
    private readonly Dictionary<(long EdgeId, int KeyId), long> _edgePayloads = new();
    private PayloadLaneSpec? _payloadSpec;
    private bool _committed;

    private record struct PendingVertex(long Id, int LabelId);
    private record struct PendingEdge(long Id, long Src, long Tgt, int TypeId);
    private readonly record struct PendingProp(int KeyId, PropertyValueType Type, long Scalar, byte[]? Data);

    internal BulkLoader(VersionedVertexStore vertexStore, VersionedEdgeStore edgeStore, PropertyVersionStore propStore,
        Quiver.Storage.SingleFileContainer? container = null)
    {
        _vertexStore = vertexStore;
        _edgeStore = edgeStore;
        _propStore = propStore;
        _container = container;
    }

    /// <summary>Vertexを追加する (ラベル付き)。</summary>
    public void AppendVertex(VertexId id, LabelId label)
    {
        ThrowIfCommitted();
        // 物理 slot は Sequence (利用側が gen 付き id を渡しても正しく正規化)。
        _vertices.Add(new PendingVertex(id.Sequence, label.Value));
    }

    /// <summary>Edgeを追加する (順不同で可)。</summary>
    public void AppendEdge(EdgeId id, VertexId from, VertexId to, EdgeTypeId type)
    {
        ThrowIfCommitted();
        _edges.Add(new PendingEdge(id.Sequence, from.Sequence, to.Sequence, type.Value));
    }

    /// <summary>Vertexにプロパティを追加する。</summary>
    public void AppendProperty(VertexId vertexId, PropertyKeyId key, in PropertyValue value)
    {
        ThrowIfCommitted();
        byte[]? data = null;
        if (value.Type is PropertyValueType.String)
            data = value.Utf8StringValue.ToArray();
        else if (value.Type is PropertyValueType.Bytes)
            data = value.BytesValue.ToArray();

        if (!_propsByVertex.TryGetValue(vertexId.Sequence, out var props))
            _propsByVertex[vertexId.Sequence] = props = new();
        props.Add(new PendingProp(key.Value, value.Type, value.Int64Value, data));
    }

    /// <summary>
    /// inline payload lane を設定し、<see cref="Commit"/> で指定したEdgeプロパティを
    /// エッジエントリ毎に inline 格納した <c>AdjacencySegmentStore</c> を構築させる
    /// 。以後の <see cref="AppendEdgePayload"/> で lane を埋め、
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
    /// Edgeの inline payload 値を記録する。<see cref="WithPayloadLane"/> の spec と
    /// キーが一致する値のみが adjacency segment に inline され、他キーは破棄される。生の long は lane の
    /// 種別に応じて Int64 値または <c>BitConverter.DoubleToInt64Bits(d)</c>。
    /// </summary>
    public void AppendEdgePayload(EdgeId edgeId, PropertyKeyId key, long rawValue)
    {
        ThrowIfCommitted();
        _edgePayloads[(edgeId.Sequence, key.Value)] = rawValue;
    }

    /// <summary>溜めたVertex / Edge / プロパティを 1 回のフラッシュで書き出し確定する。</summary>
    public void Commit()
    {
        ThrowIfCommitted();
        _committed = true;

        CommitVertices();
        CommitEdges();
        CommitProperties();
        if (_container != null)
            BuildAdjacencyIndex(_container);
    }

    /// <summary>ローダを破棄する (現状は no-op)。</summary>
    public void Dispose() { }

    // -----------------------------------------------------------------------

    private void CommitVertices()
    {
        long hwm = 0;
        foreach (var vertex in _vertices)
        {
            _vertexStore.BulkWrite(vertex.Id, vertex.LabelId);
            if (vertex.Id >= hwm) hwm = vertex.Id + 1;
        }
        _vertexStore.BulkSetHeaders(hwm, _vertices.Count);
    }

    // 単一パス dense-array ポインタ計算。従来の 2 パス
    // Dictionary<long, List<(long, bool)>> + Dictionary<long, (long, long, long, long)>
    // 方式 (10M エッジで ~700+ MB 割り当て) を置き換える。アルゴリズム:
    //
    //   EdgeId 昇順で各 edge r について、各接触Vertexのチェーンは src 側と tgt 側の edge を
    //   交互配置する。Vertexごとに直近の edge と側を追跡し、同一Vertexに新しい edge が来たら
    //   (1) Next フィールドを前の末尾に向け、(2) 前の末尾の Prev フィールドを適切な側で
    //   パッチする。各Vertexの FirstEdgeId は最終的な lastByVertex[vertex] (最大 EdgeId)。
    //
    // Self-loop: src 側のみをチェーンに記録する。書き込み時 tgt 側のフィールドは src を
    // ミラーする — 従来の実装と同じセマンティクス。
    private void CommitEdges()
    {
        if (_edges.Count == 0)
        {
            _edgeStore.BulkSetHeaders(0, 0);
            return;
        }

        long relHwm = 0, vertexHwm = 0;
        foreach (var r in _edges)
        {
            if (r.Id  >= relHwm)  relHwm  = r.Id  + 1;
            if (r.Src >= vertexHwm) vertexHwm = r.Src + 1;
            if (r.Tgt >= vertexHwm) vertexHwm = r.Tgt + 1;
        }

        // チェーンポインタを正しい方向にパッチするため、EdgeId 昇順で処理する必要がある。
        // 呼び出し側は通常シーケンシャルに append するのでソートはほぼ no-op だが、
        // 防御的にソートする。
        _edges.Sort((a, b) => a.Id.CompareTo(b.Id));

        var srcPrev = new long[relHwm];
        var srcNext = new long[relHwm];
        var tgtPrev = new long[relHwm];
        var tgtNext = new long[relHwm];
        Array.Fill(srcPrev, -1L);
        Array.Fill(srcNext, -1L);
        Array.Fill(tgtPrev, -1L);
        Array.Fill(tgtNext, -1L);

        var lastByVertex = new long[vertexHwm];
        var lastSide   = new byte[vertexHwm]; // 0 = src at this vertex, 1 = tgt
        Array.Fill(lastByVertex, -1L);

        foreach (var r in _edges)
        {
            long prev = lastByVertex[r.Src];
            if (prev >= 0)
            {
                if (lastSide[r.Src] == 0) srcPrev[prev] = r.Id;
                else                      tgtPrev[prev] = r.Id;
            }
            srcNext[r.Id] = prev;
            lastByVertex[r.Src] = r.Id;
            lastSide[r.Src] = 0;

            if (r.Tgt != r.Src)
            {
                long prevT = lastByVertex[r.Tgt];
                if (prevT >= 0)
                {
                    if (lastSide[r.Tgt] == 0) srcPrev[prevT] = r.Id;
                    else                      tgtPrev[prevT] = r.Id;
                }
                tgtNext[r.Id] = prevT;
                lastByVertex[r.Tgt] = r.Id;
                lastSide[r.Tgt] = 1;
            }
        }

        long hwm = 0;
        foreach (var r in _edges)
        {
            long sp = srcPrev[r.Id], sn = srcNext[r.Id];
            long tp, tn;
            if (r.Src == r.Tgt) { tp = sp; tn = sn; }
            else                { tp = tgtPrev[r.Id]; tn = tgtNext[r.Id]; }

            _edgeStore.BulkWrite(r.Id, r.Src, r.Tgt, r.TypeId, sp, sn, tp, tn);
            if (r.Id >= hwm) hwm = r.Id + 1;
        }
        _edgeStore.BulkSetHeaders(hwm, _edges.Count);

        for (long n = 0; n < vertexHwm; n++)
        {
            long head = lastByVertex[n];
            if (head >= 0)
                _vertexStore.UpdateFirstEdgeId(new VertexId(n), new EdgeId(head));
        }
    }

    private void CommitProperties()
    {
        foreach (var (vertexId, props) in _propsByVertex)
        {
            // tail-to-head でチェーンを構築する。最後に書き込まれたプロパティが head になる。
            long nextPropertySequence = -1L;
            var owner = EntityRef.From(VertexId.Create(vertexId, _vertexStore.CurrentGeneration(vertexId)));
            foreach (var prop in props)
            {
                var propertyVersion = _propStore.BulkCreate(
                    owner, prop.KeyId, PropertyCardinality.Single, prop.Type, prop.Scalar, prop.Data, nextPropertySequence);
                nextPropertySequence = propertyVersion.Sequence; // Int48 chain link は Sequence
            }
            _vertexStore.BulkUpdateFirstPropertyRef(vertexId, nextPropertySequence);
        }
        _propStore.BulkFlushMeta();
    }

    private void BuildAdjacencyIndex(Quiver.Storage.SingleFileContainer container)
    {
        long vertexHwm = _vertices.Count > 0 ? _vertices.Max(n => n.Id) + 1 : 0L;
        long relHwm = _edges.Count > 0 ? _edges.Max(r => r.Id) + 1 : 0L;
        var relData = _edges.Select(r => (r.Id, r.Src, r.Tgt, r.TypeId)).ToList();

        Dictionary<long, long>? weights = null;
        if (_payloadSpec is { } spec)
        {
            // 設定されたキーに payload をフィルタする。raw 値はそのまま渡される
            // (double<->long の再解釈は AppendEdgePayload 経由で呼び出し側の責任)。
            weights = new Dictionary<long, long>(_edgePayloads.Count);
            foreach (var ((edgeId, keyId), raw) in _edgePayloads)
            {
                if (keyId == spec.PropertyKeyId)
                    weights[edgeId] = raw;
            }
        }

        // relHwm を base watermark として記録し、post-bulk-load の delta
        // (id >= relHwm) を読み取り時に base ビューへ二重計上せず merge できるようにする。
        AdjacencyContainer.Build(container, relData, vertexHwm, relHwm, _payloadSpec, weights);
    }

    private void ThrowIfCommitted()
    {
        if (_committed) throw new InvalidOperationException("BulkLoader has already been committed.");
    }

}
