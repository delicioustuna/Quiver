using System.Buffers.Binary;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// 1000 万エッジ超の取り込みを想定した <see cref="BulkLoader"/> のストリーミング版。
/// <c>AppendEdge</c> 中はEdgeレコードを <c>List&lt;PendingEdge&gt;</c> に溜めず、
/// 一時バイナリファイルにストリーミング書き出しする。<see cref="Commit"/> で一時ファイルを 2 回読み出す —
/// 1 回目で双方向リンクチェーンのポインタを dense <c>long[]</c> 配列 (maxEdgeId+1 / maxVertexId+1 サイズ) に
/// 計算し、2 回目で <c>EdgeStore.BulkWrite</c> を EdgeId 昇順で発行する。
///
/// 事前条件: <see cref="AppendEdge"/> は <see cref="EdgeId"/> が厳密に増加する順序で呼ぶ必要がある。
/// 典型的なバルクインポートでは順次 ID が割り当てられるためこの前提が成り立ち、ポインタ計算パスで
/// 別途ソート工程を省略できる。
///
/// 1000 万エッジ時のメモリフットプリント (adj 構築なし):
///   - ディスク上の一時ファイル:                          280 MB (28 B/edge)
///   - ポインタ配列 (4 × long[maxEdgeId+1]):             約 320 MB
///   - lastByVertex / lastSide (Vertex毎 long[] + byte[]): 100 万Vertexで約 9 MB
///   合計ヒープ: 約 330 MB (旧 <c>BulkLoader</c> 約 1.7 GB と比較)。
///
/// Vertexとプロパティは引き続きヒープ上 (一般的な負荷では小さい)。隣接インデックス構築は
/// 一時ファイルをコンパクトリストとして 1 回再読み込みする — これが唯一エッジ数に対して
/// 線形にヒープが伸びる経路。1000 万件超の取り込み計画がある呼び出し側は
/// <c>buildAdjacencyIndex: false</c> を選び、必要なら後段で <c>QuiverDatabase.CompactAdjacency()</c> を
/// 呼ぶことを推奨する。
/// </summary>
public sealed class StreamingBulkLoader : IDisposable
{
    private readonly VersionedVertexStore _vertexStore;
    private readonly VersionedEdgeStore _edgeStore;
    private readonly PropertyStore _propStore;
    // 隣接ビューは graph.quiver 内テナントへ構築する (null = 構築しない)。
    private readonly Quiver.Storage.SingleFileContainer? _container;

    private const int EdgeRecordSize = 28; // Id(8) + Src(8) + Tgt(8) + TypeId(4)

    private readonly string _tempPath;
    private readonly FileStream _edgeTemp;
    private readonly byte[] _writeBuf = new byte[EdgeRecordSize];

    private long _edgeCount;
    private long _maxEdgeId = -1;
    private long _maxVertexId = -1;

    private readonly List<PendingVertex> _vertices = new();
    private readonly Dictionary<long, List<PendingProp>> _propsByVertex = new();
    private readonly Dictionary<(long EdgeId, int KeyId), long> _edgePayloads = new();
    private PayloadLaneSpec? _payloadSpec;
    private bool _committed;
    private bool _disposed;

    private record struct PendingVertex(long Id, int LabelId);
    private readonly record struct PendingProp(int KeyId, PropertyValueType Type, long Scalar, byte[]? Data);

    internal StreamingBulkLoader(
        VersionedVertexStore vertexStore, VersionedEdgeStore edgeStore, PropertyStore propStore,
        Quiver.Storage.SingleFileContainer? container = null)
    {
        _vertexStore = vertexStore;
        _edgeStore = edgeStore;
        _propStore = propStore;
        _container = container;

        _tempPath = Path.Combine(
            Path.GetTempPath(),
            "quiver_streaming_bulk_" + Guid.NewGuid().ToString("N")[..16] + ".edges");
        _edgeTemp = new FileStream(
            _tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.DeleteOnClose);
    }

    /// <summary>Vertexを追加する (ラベル付き)。</summary>
    public void AppendVertex(VertexId id, LabelId label)
    {
        ThrowIfCommitted();
        // 物理 slot は Sequence (利用側が gen 付き id を渡しても正しく正規化)。
        _vertices.Add(new PendingVertex(id.Sequence, label.Value));
        if (id.Sequence > _maxVertexId) _maxVertexId = id.Sequence;
    }

    /// <summary>
    /// Edgeを追加する。<paramref name="id"/> は厳密に増加する順序で渡す必要がある
    /// (順不同入力には <see cref="BulkLoader"/> を使う)。
    /// </summary>
    public void AppendEdge(EdgeId id, VertexId from, VertexId to, EdgeTypeId type)
    {
        ThrowIfCommitted();
        if (id.Sequence <= _maxEdgeId)
            throw new InvalidOperationException(
                $"StreamingBulkLoader requires AppendEdge in strictly increasing " +
                $"EdgeId order (got {id.Sequence}, last was {_maxEdgeId}). " +
                $"Use BulkLoader for unordered input.");

        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(0),  id.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(8),  from.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(_writeBuf.AsSpan(16), to.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(_writeBuf.AsSpan(24), type.Value);
        _edgeTemp.Write(_writeBuf, 0, EdgeRecordSize);

        _edgeCount++;
        _maxEdgeId = id.Sequence;
        if (from.Sequence > _maxVertexId) _maxVertexId = from.Sequence;
        if (to.Sequence   > _maxVertexId) _maxVertexId = to.Sequence;
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

        if (!_propsByVertex.TryGetValue(vertexId.Sequence, out var props)) // key は Sequence
            _propsByVertex[vertexId.Sequence] = props = new();
        props.Add(new PendingProp(key.Value, value.Type, value.Int64Value, data));
    }

    /// <summary>隣接インデックスに inline する payload lane を設定する (Kind は Int64 / Double のみ)。</summary>
    public void WithPayloadLane(PayloadLaneSpec spec)
    {
        ThrowIfCommitted();
        if (spec.Kind == PayloadKind.None)
            throw new ArgumentException("PayloadLaneSpec.Kind must be Int64 or Double.", nameof(spec));
        _payloadSpec = spec;
    }

    /// <summary>Edgeの payload lane 値 (生の 64bit) を追加する。</summary>
    public void AppendEdgePayload(EdgeId edgeId, PropertyKeyId key, long rawValue)
    {
        ThrowIfCommitted();
        _edgePayloads[(edgeId.Sequence, key.Value)] = rawValue; // edge key は Sequence
    }

    /// <summary>溜めたVertex / Edge / プロパティをストアへ書き出し確定する。</summary>
    public void Commit()
    {
        ThrowIfCommitted();
        _committed = true;

        _edgeTemp.Flush();

        CommitVertices();
        CommitEdgesStreaming();
        CommitProperties();
        if (_container != null)
            BuildAdjacencyIndexStreaming(_container);
    }

    /// <summary>一時ファイルを破棄する (<see cref="Commit"/> 有無に関わらずクリーンアップする)。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _edgeTemp.Dispose(); } // FileOptions.DeleteOnClose handles cleanup
        catch { /* swallow — temp file cleanup is best-effort */ }
    }

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

    private void CommitEdgesStreaming()
    {
        if (_edgeCount == 0)
        {
            _edgeStore.BulkSetHeaders(0, 0);
            return;
        }

        long relHwm = _maxEdgeId + 1;
        long vertexHwm = _maxVertexId + 1;

        var srcPrev = new long[relHwm]; Array.Fill(srcPrev, -1L);
        var srcNext = new long[relHwm]; Array.Fill(srcNext, -1L);
        var tgtPrev = new long[relHwm]; Array.Fill(tgtPrev, -1L);
        var tgtNext = new long[relHwm]; Array.Fill(tgtNext, -1L);

        var lastByVertex = new long[vertexHwm]; Array.Fill(lastByVertex, -1L);
        var lastSide   = new byte[vertexHwm]; // 0 = was src at this vertex, 1 = was tgt

        // パス 1: 単一前方走査でポインタを計算する。アルゴリズムは in-memory BulkLoader
        // (BulkLoader.CommitEdges 参照) と同一 — 各Vertexのチェーンは側に関係なく
        // Edgeを交互配置する。ポインタが書き込まれるフィールドは、そのVertexでの
        // Edgeの側 (src/tgt) に依存する。
        var buf = new byte[EdgeRecordSize];
        _edgeTemp.Position = 0;
        for (long i = 0; i < _edgeCount; i++)
        {
            _edgeTemp.ReadExactly(buf, 0, EdgeRecordSize);
            long id  = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));

            long prev = lastByVertex[src];
            if (prev >= 0)
            {
                if (lastSide[src] == 0) srcPrev[prev] = id;
                else                    tgtPrev[prev] = id;
            }
            srcNext[id] = prev;
            lastByVertex[src] = id;
            lastSide[src] = 0;

            if (tgt != src)
            {
                long prevT = lastByVertex[tgt];
                if (prevT >= 0)
                {
                    if (lastSide[tgt] == 0) srcPrev[prevT] = id;
                    else                    tgtPrev[prevT] = id;
                }
                tgtNext[id] = prevT;
                lastByVertex[tgt] = id;
                lastSide[tgt] = 1;
            }
        }

        // パス 2: Edgeを再度ストリームし、dense lookup でポインタを適用する。
        long hwm = 0;
        _edgeTemp.Position = 0;
        for (long i = 0; i < _edgeCount; i++)
        {
            _edgeTemp.ReadExactly(buf, 0, EdgeRecordSize);
            long id     = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));
            int  typeId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24));

            long sp = srcPrev[id], sn = srcNext[id];
            long tp, tn;
            if (src == tgt) { tp = sp; tn = sn; }
            else            { tp = tgtPrev[id]; tn = tgtNext[id]; }

            _edgeStore.BulkWrite(id, src, tgt, typeId, sp, sn, tp, tn);
            if (id >= hwm) hwm = id + 1;
        }
        _edgeStore.BulkSetHeaders(hwm, _edgeCount);

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
            long nextPropId = -1L;
            foreach (var prop in props)
            {
                var propId = _propStore.BulkCreate(prop.KeyId, prop.Type, prop.Scalar, prop.Data, nextPropId);
                nextPropId = propId.Sequence; // Int48 NextPropId は Sequence
            }
            _vertexStore.BulkUpdateFirstProp(vertexId, nextPropId);
        }
        _propStore.BulkFlushMeta();
    }

    private void BuildAdjacencyIndexStreaming(Quiver.Storage.SingleFileContainer container)
    {
        // AdjacencyContainer.Build 用にEdgeをコンパクトリストへ実体化する。
        // 完全ストリーミング隣接構築 (src/tgt による chunk-sort) は将来課題。
        var relData = new List<(long Id, long Src, long Tgt, int TypeId)>((int)Math.Min(_edgeCount, int.MaxValue));
        var buf = new byte[EdgeRecordSize];
        _edgeTemp.Position = 0;
        for (long i = 0; i < _edgeCount; i++)
        {
            _edgeTemp.ReadExactly(buf, 0, EdgeRecordSize);
            long id     = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0));
            long src    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            long tgt    = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));
            int  typeId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(24));
            relData.Add((id, src, tgt, typeId));
        }

        long vertexHwm = _maxVertexId + 1;
        long relHwm = _maxEdgeId + 1;

        Dictionary<long, long>? weights = null;
        if (_payloadSpec is { } spec)
        {
            weights = new Dictionary<long, long>(_edgePayloads.Count);
            foreach (var ((edgeId, keyId), raw) in _edgePayloads)
                if (keyId == spec.PropertyKeyId)
                    weights[edgeId] = raw;
        }
        AdjacencyContainer.Build(container, relData, vertexHwm, relHwm, _payloadSpec, weights);
    }

    private void ThrowIfCommitted()
    {
        if (_committed)
            throw new InvalidOperationException("StreamingBulkLoader has already been committed.");
    }
}
