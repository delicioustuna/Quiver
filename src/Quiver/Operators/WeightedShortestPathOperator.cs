using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 重み付き最短経路オペレータ — 単一実装で Dijkstra と A* を兼ねる。
/// 入力の各 <c>(source, target)</c> ペアについてエッジ重み合計が最小の経路を探索し、
/// <c>(source, target, distance, path)</c> の 4 列を放出する。経路が無いペアは
/// <see cref="ShortestPathOperator"/> 同様スキップする。
/// </summary>
/// <remarks>
/// <para>
/// 探索は優先度キュー (<see cref="PriorityQueue{TElement,TPriority}"/>) 駆動の標準
/// settled-set 法。1 ホップ展開は <see cref="OneHopExpansion"/> 経由で
/// <see cref="WeightedShortestPathKernel"/> に委譲し、バックエンドのアクセス経路
/// (隣接ブロック / リンクリスト / Edgeスキャン) に依存しない。
/// </para>
/// <para>
/// ヒューリスティック (<c>heuristic</c>) を渡すと A*、渡さない (null) と Dijkstra に
/// 縮退する。優先度は <c>g(n)</c> (確定重み) と <c>g(n)+h(n)</c> を切り替えるが、
/// 経路復元と確定距離は常に <c>g(n)</c> のみを使う。settled-set 法を採るため、
/// A* のヒューリスティックは consistent (単調) であることを前提とする
/// (直線/大圏距離は consistent)。エッジ重みは非負でなければならない — 負の重みを
/// 検出すると <see cref="InvalidOperationException"/> を投げる。
/// </para>
/// <para>
/// 可変長の経路は固定長 <see cref="TupleSlot"/> に載らないため、
/// <c>path</c> 列を <see cref="TupleSlotType.Bytes"/> とし、
/// <see cref="WeightedPathCodec"/> でシリアライズして <see cref="GetBytes"/> で公開する。
/// </para>
/// </remarks>
internal sealed class WeightedShortestPathOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly int _tgtCol;
    private readonly Direction _dir;
    private readonly EdgeTypeId? _typeFilter;
    private readonly IEdgeWeightProvider _weightProvider;
    private readonly ITransactionVertexHeuristic? _heuristic;
    private readonly double _maxDistance;

    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[4];
    private byte[] _pathBytes = [];
    private WeightedShortestPathState _state;
    private WeightedShortestPathKernel? _kernel;

    /// <summary>
    /// これまでに確定 (settled) したVertexの累計。Dijkstra と A* の探索効率
    /// (A* がVertex展開数を削減できているか) を比較するために公開する。
    /// </summary>
    public long ExpandedVertexCount { get; private set; }

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("source",   TupleSlotType.VertexId),
        new ColumnDefinition("target",   TupleSlotType.VertexId),
        new ColumnDefinition("distance", TupleSlotType.Double),
        new ColumnDefinition("path",     TupleSlotType.Bytes)]);

    /// <param name="source">入力 <c>(source, target)</c> ペアを供給する上流オペレータ。</param>
    /// <param name="sourceVertexColumn">上流タプルの始点Vertex列インデックス。</param>
    /// <param name="targetVertexColumn">上流タプルの終点Vertex列インデックス。</param>
    /// <param name="direction">辿る方向。</param>
    /// <param name="typeFilter">辿るEdge型 (null なら全型)。</param>
    /// <param name="weightProvider">エッジ重みの取得経路。</param>
    /// <param name="heuristic">
    /// A* の推定残コスト <c>h(n)</c>。null なら Dijkstra。consistent (単調) かつ
    /// 非負であることが、settled-set 法で最適解を保証する前提。
    /// </param>
    /// <param name="maxDistance">この重み合計を超える経路は探索しない (既定: 無制限)。</param>
    public WeightedShortestPathOperator(
        IPhysicalOperator source,
        int sourceVertexColumn,
        int targetVertexColumn,
        Direction direction,
        EdgeTypeId? typeFilter,
        IEdgeWeightProvider weightProvider,
        Func<VertexId, double>? heuristic = null,
        double maxDistance = double.PositiveInfinity)
        : this(
            source,
            sourceVertexColumn,
            targetVertexColumn,
            direction,
            typeFilter,
            weightProvider,
            heuristic is null ? null : new DelegateVertexHeuristic(heuristic),
            maxDistance)
    {
    }

    internal WeightedShortestPathOperator(
        IPhysicalOperator source,
        int sourceVertexColumn,
        int targetVertexColumn,
        Direction direction,
        EdgeTypeId? typeFilter,
        IEdgeWeightProvider weightProvider,
        ITransactionVertexHeuristic? transactionHeuristic,
        double maxDistance = double.PositiveInfinity)
    {
        _source = source;
        _srcCol = sourceVertexColumn;
        _tgtCol = targetVertexColumn;
        _dir = direction;
        _typeFilter = typeFilter;
        _weightProvider = weightProvider ?? throw new ArgumentNullException(nameof(weightProvider));
        _heuristic = transactionHeuristic;
        _maxDistance = maxDistance;
    }

    /// <inheritdoc/>
    public TupleSchema Schema => s_schema;

    /// <inheritdoc/>
    public OperatorStatistics Statistics { get; private set; }

    /// <inheritdoc/>
    public TupleRef Current => new(_buffer);

    /// <summary>
    /// 直近に <see cref="MoveNext"/> が放出した行の <c>path</c> 列 (列 3) を返す。
    /// それ以外の列は空。<see cref="WeightedPathCodec.Decode"/> でデコードする。
    /// </summary>
    public ReadOnlySpan<byte> GetBytes(int column)
        => column == 3 ? _pathBytes : ReadOnlySpan<byte>.Empty;

    /// <inheritdoc/>
    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _state = default;
        ExpandedVertexCount = 0;
        _kernel = new WeightedShortestPathKernel(tx, _weightProvider, _heuristic, _maxDistance);
    }

    /// <inheritdoc/>
    public bool MoveNext()
    {
        while (_source.MoveNext())
        {
            var src = new VertexId(_source.Current[_srcCol].LongValue);
            var tgt = new VertexId(_source.Current[_tgtCol].LongValue);

            if (!FindShortestPath(src, tgt, out double dist))
                continue;

            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = src.Value };
            _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = tgt.Value };
            _buffer[2] = new TupleSlot { Type = TupleSlotType.Double, DoubleValue = dist };
            _buffer[3] = new TupleSlot { Type = TupleSlotType.Bytes };

            var s = Statistics;
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    private bool FindShortestPath(VertexId src, VertexId tgt, out double distance)
    {
        if (src == tgt)
        {
            distance = 0.0;
            _pathBytes = WeightedPathCodec.Encode([src], []);
            return true;
        }

        _kernel!.Initialize(src, ref _state);

        while (_state.Pq!.TryDequeue(out long vertex, out _))
        {
            if (!_state.Settled!.Add(vertex)) continue;   // 既に確定済の重複エントリ
            ExpandedVertexCount++;
            if (vertex == tgt.Sequence) break;            // 確定 = 最短重み距離 (slot 同一性)
            OneHopExpansion.Expand(_tx!, new VertexId(vertex), _dir, _typeFilter, depth: 0, _kernel, ref _state);
        }

        // 終点が settled に入っていれば Dist[終点] が最短重み距離。
        // PQ を出ても settled に無ければ (= 重み maxDistance 以内では) 到達不能。
        if (!_state.Settled!.Contains(tgt.Sequence))
        {
            distance = 0.0;
            return false;
        }
        distance = _state.Dist![tgt.Sequence];
        _pathBytes = ReconstructPath(src, tgt);
        return true;
    }

    private byte[] ReconstructPath(VertexId src, VertexId tgt)
    {
        // 内部の距離/前任マップは slot 同一性 (Sequence) でキーされる。
        var vertices = new List<long> { tgt.Sequence };
        var edges = new List<long>();
        long cur = tgt.Sequence;
        while (cur != src.Sequence)
        {
            var (predVertex, edgeId) = _state.Pred![cur];
            edges.Add(edgeId);
            vertices.Add(predVertex);
            cur = predVertex;
        }
        vertices.Reverse();
        edges.Reverse();

        var materializer = new EntityIdentityMaterializer(
            _tx!.Vertices, _tx.Edges, _tx.Nexuses);
        var vertexIds = new VertexId[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            if (!materializer.TryVertex(new VertexId(vertices[i]), out vertexIds[i]))
                return [];
        }
        var edgeIds = new EdgeId[edges.Count];
        for (int i = 0; i < edges.Count; i++)
        {
            if (!materializer.TryEdge(new EdgeId(edges[i]), out edgeIds[i]))
                return [];
        }
        return WeightedPathCodec.Encode(vertexIds, edgeIds);
    }

    /// <inheritdoc/>
    public void Dispose() => _source.Dispose();
}

/// <summary>
/// <see cref="WeightedShortestPathOperator"/> の <c>path</c> 列 (列 3) のバイト列コーデック。
/// レイアウト (すべて little-endian): <c>[int32 vertexCount][int32 edgeCount]
/// [vertexCount × int64 VertexId][edgeCount × int64 EdgeId]</c>。
/// </summary>
internal static class WeightedPathCodec
{
    /// <summary>Vertex列・Edge列をバイト列にエンコードする。</summary>
    public static byte[] Encode(IReadOnlyList<VertexId> vertices, IReadOnlyList<EdgeId> edges)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(edges);
        int n = vertices.Count, m = edges.Count;
        var buf = new byte[8 + (n + m) * 8];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, n);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], m);
        int off = 8;
        for (int i = 0; i < n; i++, off += 8)
            BinaryPrimitives.WriteInt64LittleEndian(span[off..], vertices[i].Value);
        for (int i = 0; i < m; i++, off += 8)
            BinaryPrimitives.WriteInt64LittleEndian(span[off..], edges[i].Value);
        return buf;
    }

    /// <summary>
    /// <see cref="Encode"/> が生成したバイト列をVertex列・Edge列に戻す。
    /// 8 バイト未満の入力 (空 path) では両方とも空配列を返す。
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> bytes, out VertexId[] vertices, out EdgeId[] edges)
    {
        if (bytes.Length < 8)
        {
            vertices = [];
            edges = [];
            return;
        }
        int n = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int m = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        vertices = new VertexId[n];
        edges = new EdgeId[m];
        int off = 8;
        for (int i = 0; i < n; i++, off += 8)
            vertices[i] = new VertexId(BinaryPrimitives.ReadInt64LittleEndian(bytes[off..]));
        for (int i = 0; i < m; i++, off += 8)
            edges[i] = new EdgeId(BinaryPrimitives.ReadInt64LittleEndian(bytes[off..]));
    }
}

/// <summary>
/// <see cref="WeightedShortestPathOperator"/> の per-pair 探索状態。
/// 参照型コレクションは <see cref="WeightedShortestPathKernel.Initialize"/> で
/// 遅延確保し、ペア毎に <c>Clear</c> して再利用する。
/// </summary>
internal struct WeightedShortestPathState
{
    /// <summary>Vertex → 既知の最小重み距離 <c>g(n)</c>。</summary>
    public Dictionary<long, double>? Dist;

    /// <summary>Vertex → (1 つ前のVertex, そのエッジ) — 経路復元用。</summary>
    public Dictionary<long, (long Vertex, long Rel)>? Pred;

    /// <summary>確定済 (最短距離が判明した) Vertex集合。</summary>
    public HashSet<long>? Settled;

    /// <summary>優先度キュー (priority = Dijkstra: g(n) / A*: g(n)+h(n))。</summary>
    public PriorityQueue<long, double>? Pq;
}

/// <summary>
/// <see cref="WeightedShortestPathOperator"/> 用の <see cref="IGraphKernel{TState}"/> 実装。
/// <see cref="VisitNeighbor"/> がエッジ relaxation を行う。frontier スケジューリング
/// (PQ の pop) はオペレータ側が担当する。
/// </summary>
internal sealed class WeightedShortestPathKernel(
    ITransaction tx,
    IEdgeWeightProvider weightProvider,
    ITransactionVertexHeuristic? heuristic,
    double maxDistance) : IGraphKernel<WeightedShortestPathState>
{
    /// <inheritdoc/>
    public void Initialize(VertexId source, ref WeightedShortestPathState s)
    {
        (s.Dist ??= []).Clear();
        (s.Pred ??= []).Clear();
        (s.Settled ??= []).Clear();
        (s.Pq ??= new PriorityQueue<long, double>()).Clear();

        // PQ / Dist / Pred / Settled は slot 同一性 (Sequence) でキーされる。
        s.Dist[source.Sequence] = 0.0;
        s.Pq.Enqueue(source.Sequence, Heuristic(source));
    }

    /// <inheritdoc/>
    public bool VisitNeighbor(
        VertexId source, VertexId target, EdgeId edgeId,
        long weightRaw, int depth, ref WeightedShortestPathState s)
    {
        if (s.Settled!.Contains(target.Sequence)) return true;   // 確定済Vertexは緩和不要

        double w = weightProvider.GetWeight(tx, edgeId, weightRaw);
        if (w < 0.0)
            throw new InvalidOperationException(
                $"重み付き最短経路 (Dijkstra/A*) は負のエッジ重みをサポートしません " +
                $"(edge {edgeId.Value}, weight {w})。負の重みには Bellman-Ford 系が必要です。");

        double cand = s.Dist![source.Sequence] + w;
        if (cand > maxDistance) return true;

        if (cand < s.Dist.GetValueOrDefault(target.Sequence, double.PositiveInfinity))
        {
            s.Dist[target.Sequence] = cand;
            s.Pred![target.Sequence] = (source.Sequence, edgeId.Sequence);
            s.Pq!.Enqueue(target.Sequence, cand + Heuristic(target));
        }
        return true;
    }

    /// <inheritdoc/>
    public bool ShouldContinue(int depth, in WeightedShortestPathState s) => true;

    private double Heuristic(VertexId vertex)
    {
        if (heuristic is null) return 0.0;
        double h = heuristic.Estimate(tx, vertex);
        // 負のヒューリスティックは admissibility を壊すため 0 にクランプする。
        return h > 0.0 ? h : 0.0;
    }
}
