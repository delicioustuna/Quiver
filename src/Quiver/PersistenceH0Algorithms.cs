using System.Diagnostics;
using Quiver.Core;

namespace Quiver;

/// <summary>0 次パーシステンス計算で使う濾過の構成方法。</summary>
public enum PersistenceH0Filtration
{
    /// <summary>全点対距離から完全グラフの Vietoris-Rips 濾過を構成する。</summary>
    Complete = 0,

    /// <summary>各点の k 近傍を無向化した疎グラフから濾過を構成する。</summary>
    SparseKnn = 1,
}

/// <summary>返された barcode の正確性と完備性。</summary>
public enum PersistenceH0ResultKind
{
    /// <summary>全点対濾過について、要求された scale までの全区間を厳密に返した。</summary>
    Exact = 0,

    /// <summary>疎 k-NN 濾過について全区間を返した。完全グラフの barcode の近似保証は持たない。</summary>
    SparseApproximation = 1,

    /// <summary>上限、時間切れ、またはキャンセルにより全区間を返していない。</summary>
    Incomplete = 2,
}

/// <summary>0 次パーシステンス計算の終了理由。</summary>
public enum PersistenceH0TerminationReason
{
    /// <summary>選択した濾過の計算と結果 materialization が完了した。</summary>
    Completed = 0,

    /// <summary>入力点数が <see cref="PersistenceH0Options.MaxPoints"/> を超えた。</summary>
    MaxPointsReached = 1,

    /// <summary>候補辺数が <see cref="PersistenceH0Options.MaxEdges"/> を超えた。</summary>
    MaxEdgesReached = 2,

    /// <summary>距離評価回数が <see cref="PersistenceH0Options.MaxDistanceEvaluations"/> を超える見込みだった。</summary>
    MaxDistanceEvaluationsReached = 3,

    /// <summary>返却区間数が <see cref="PersistenceH0Options.MaxResults"/> に達した。</summary>
    MaxResultsReached = 4,

    /// <summary><see cref="PersistenceH0Options.TimeLimit"/> に達した。</summary>
    TimeBudget = 5,

    /// <summary><see cref="PersistenceH0Options.CancellationToken"/> でキャンセルされた。</summary>
    Cancelled = 6,
}

/// <summary>0 次パーシステンス計算の上限と濾過を指定する。</summary>
public sealed record PersistenceH0Options
{
    /// <summary>濾過の構成方法。既定は疎 k-NN。</summary>
    public PersistenceH0Filtration Filtration { get; init; } = PersistenceH0Filtration.SparseKnn;

    /// <summary>疎 k-NN 濾過で各点から取得する自己以外の近傍数。</summary>
    public int NeighborCount { get; init; } = 16;

    /// <summary>snapshot から materialize する最大点数。</summary>
    public int MaxPoints { get; init; } = 10_000;

    /// <summary>構築する無向辺の最大数。</summary>
    public int MaxEdges { get; init; } = 1_000_000;

    /// <summary>距離評価の最大回数。疎 k-NN の primary scan では点数の二乗を見積もる。</summary>
    public long MaxDistanceEvaluations { get; init; } = 10_000_000;

    /// <summary>返却する区間の最大数。</summary>
    public int MaxResults { get; init; } = 100_000;

    /// <summary>計算の協調的な時間上限。</summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>計算を協調的に中止するトークン。</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// 辺を追加する最大距離。省略時は構成した濾過の全辺を使う。
    /// 指定時に上限で生存する区間は右打ち切りとして返す。
    /// </summary>
    public float? MaxScale { get; init; }
}

/// <summary>0 次 barcode の一区間。</summary>
/// <param name="Birth">成分が生まれる scale。H0 では常に 0。</param>
/// <param name="Death">別成分へ併合される scale。生存区間では <c>null</c>。</param>
/// <param name="IsRightCensored"><paramref name="Death"/> が無い理由が scale 上限による右打ち切りなら <c>true</c>。</param>
public readonly record struct PersistenceH0Interval(float Birth, float? Death, bool IsRightCensored);

/// <summary>0 次パーシステンス計算の結果。</summary>
public sealed class PersistenceH0Result
{
    internal PersistenceH0Result(
        IReadOnlyList<PersistenceH0Interval> intervals,
        int? totalIntervalCount,
        int? pointCount,
        int? componentCount,
        long distanceEvaluations,
        int edgeCount,
        PersistenceH0Filtration filtration,
        PersistenceH0ResultKind kind,
        PersistenceH0TerminationReason terminationReason,
        float? scaleLimit)
    {
        Intervals = intervals;
        TotalIntervalCount = totalIntervalCount;
        PointCount = pointCount;
        ComponentCount = componentCount;
        DistanceEvaluations = distanceEvaluations;
        EdgeCount = edgeCount;
        Filtration = filtration;
        Kind = kind;
        TerminationReason = terminationReason;
        ScaleLimit = scaleLimit;
    }

    /// <summary>materialize された区間。有限区間は death 昇順、その後に生存区間が並ぶ。</summary>
    public IReadOnlyList<PersistenceH0Interval> Intervals { get; }

    /// <summary>結果上限を適用する前の区間数。入力走査完了前に停止した場合は <c>null</c>。</summary>
    public int? TotalIntervalCount { get; }

    /// <summary>snapshot の対象インデックスに含まれる点数。入力走査完了前に停止した場合は <c>null</c>。</summary>
    public int? PointCount { get; }

    /// <summary>指定した濾過の最終 scale で残った連結成分数。濾過完了前に停止した場合は <c>null</c>。</summary>
    public int? ComponentCount { get; }

    /// <summary>実行または事前に確定した距離評価回数。</summary>
    public long DistanceEvaluations { get; }

    /// <summary>上限検査を通過して濾過へ渡した無向辺数。</summary>
    public int EdgeCount { get; }

    /// <summary>使用した濾過の構成方法。</summary>
    public PersistenceH0Filtration Filtration { get; }

    /// <summary>barcode の正確性と完備性。</summary>
    public PersistenceH0ResultKind Kind { get; }

    /// <summary>計算の終了理由。</summary>
    public PersistenceH0TerminationReason TerminationReason { get; }

    /// <summary>指定された最大 scale。省略時は <c>null</c>。</summary>
    public float? ScaleLimit { get; }

    /// <summary>選択した濾過について全区間を返したか。</summary>
    public bool IsComplete => TerminationReason == PersistenceH0TerminationReason.Completed;

    /// <summary>全点対濾過について、要求された scale まで厳密な barcode を返したか。</summary>
    public bool IsExact => Kind == PersistenceH0ResultKind.Exact;
}

/// <summary>barcode からクラスタ数を推定した別結果。</summary>
/// <param name="ClusterCount">heuristic が推定したクラスタ数。</param>
/// <param name="SeparationScale">最大 death gap の中点。非連結成分を使った場合は <c>null</c>。</param>
/// <param name="UsedDisconnectedComponents">最大 gap ではなく最終的な非連結成分数を使った場合は <c>true</c>。</param>
public sealed record PersistenceClusterEstimate(int ClusterCount, float? SeparationScale, bool UsedDisconnectedComponents);

/// <summary>vector index の snapshot 全体から 0 次パーシステンスを計算する。</summary>
public static class PersistenceH0Algorithms
{
    /// <summary>
    /// 指定 vector index が対象にする現在の snapshot 可視な全 vector から H0 barcode を計算する。
    /// Euclidean index のみを受け付け、完全グラフ経路だけを厳密結果として報告する。
    /// </summary>
    public static PersistenceH0Result Compute(
        IReadTransaction transaction,
        string indexName,
        PersistenceH0Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        options ??= new PersistenceH0Options();
        Validate(options);
        if (!transaction.Schema.TryGetIndex(indexName, out IndexInfo index)
            || index.Definition is not VectorIndexDefinition definition)
        {
            throw new VectorException($"Vector index '{indexName}' does not exist.");
        }
        if (definition.Metric != DistanceMetric.Euclidean)
        {
            throw new NotSupportedException(
                "H0 barcodeはEuclidean vector indexだけをサポートします。" +
                "CosineとDotのscoreを距離scaleとして暗黙変換しません。");
        }

        var stopwatch = Stopwatch.StartNew();
        if (StopReason(options, stopwatch) is { } initialStop)
            return Incomplete(options, initialStop);

        Materialization materialization = MaterializePopulation(
            transaction,
            definition,
            options,
            stopwatch);
        if (materialization.StopReason is { } materializationStop)
        {
            return Incomplete(
                options,
                materializationStop);
        }

        int pointCount = materialization.Owners.Count;
        if (pointCount == 0)
        {
            return new([], 0, 0, 0, 0, 0, options.Filtration,
                options.Filtration == PersistenceH0Filtration.Complete
                    ? PersistenceH0ResultKind.Exact
                    : PersistenceH0ResultKind.SparseApproximation,
                PersistenceH0TerminationReason.Completed,
                options.MaxScale);
        }

        long distanceEvaluations = options.Filtration == PersistenceH0Filtration.Complete
            ? checked((long)pointCount * (pointCount - 1) / 2)
            : checked((long)pointCount * pointCount);
        if (distanceEvaluations > options.MaxDistanceEvaluations)
        {
            return Incomplete(options,
                PersistenceH0TerminationReason.MaxDistanceEvaluationsReached,
                pointCount,
                distanceEvaluations);
        }

        EdgeBuild edgeBuild = options.Filtration == PersistenceH0Filtration.Complete
            ? BuildCompleteEdges(materialization.Vectors, options, stopwatch)
            : BuildSparseEdges(transaction, indexName, materialization, options, stopwatch);
        if (edgeBuild.StopReason is { } edgeStop)
        {
            return Incomplete(options, edgeStop, pointCount,
                distanceEvaluations, edgeBuild.Edges.Count);
        }

        Edge[] ordered = edgeBuild.Edges
            .OrderBy(static edge => edge.Distance)
            .ThenBy(static edge => edge.Left)
            .ThenBy(static edge => edge.Right)
            .ToArray();
        if (StopReason(options, stopwatch) is { } sortStop)
            return Incomplete(options, sortStop, pointCount, distanceEvaluations, ordered.Length);

        var union = new UnionFind(pointCount);
        var deaths = new List<float>(Math.Max(0, pointCount - 1));
        for (int i = 0; i < ordered.Length; i++)
        {
            if ((i & 255) == 0 && StopReason(options, stopwatch) is { } unionStop)
                return Incomplete(options, unionStop, pointCount, distanceEvaluations, ordered.Length);
            Edge edge = ordered[i];
            if (options.MaxScale is float maxScale && edge.Distance > maxScale)
                break;
            if (union.Union(edge.Left, edge.Right))
                deaths.Add(edge.Distance);
        }

        int totalIntervals = pointCount;
        int returned = Math.Min(totalIntervals, options.MaxResults);
        var intervals = new PersistenceH0Interval[returned];
        int position = 0;
        for (; position < deaths.Count && position < returned; position++)
            intervals[position] = new(0, deaths[position], false);
        bool rightCensored = options.MaxScale.HasValue;
        for (; position < returned; position++)
            intervals[position] = new(0, null, rightCensored);

        bool resultLimited = returned < totalIntervals;
        PersistenceH0TerminationReason reason = resultLimited
            ? PersistenceH0TerminationReason.MaxResultsReached
            : PersistenceH0TerminationReason.Completed;
        PersistenceH0ResultKind kind = resultLimited
            ? PersistenceH0ResultKind.Incomplete
            : options.Filtration == PersistenceH0Filtration.Complete
                ? PersistenceH0ResultKind.Exact
                : PersistenceH0ResultKind.SparseApproximation;
        return new(intervals, totalIntervals, pointCount, union.Components,
            distanceEvaluations, ordered.Length, options.Filtration, kind, reason, options.MaxScale);
    }

    /// <summary>
    /// 完備な barcode から、非連結なら成分数、連結なら有限 death の最大 gap によりクラスタ数を推定する。
    /// barcode 自体とは独立した heuristic 結果であり、厳密クラスタ数を保証しない。
    /// </summary>
    public static PersistenceClusterEstimate EstimateClusters(PersistenceH0Result barcode)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        if (!barcode.IsComplete || barcode.Intervals.Count != barcode.TotalIntervalCount)
            throw new InvalidOperationException("クラスタ推定には全区間を含む完備なbarcodeが必要です。");
        if (barcode.ScaleLimit.HasValue)
            throw new InvalidOperationException("scaleで右打ち切りされたbarcodeからクラスタ数を推定できません。");
        if (barcode.PointCount == 0)
            return new(0, null, false);
        if (barcode.ComponentCount > 1)
            return new(barcode.ComponentCount.Value, null, true);

        float[] deaths = barcode.Intervals
            .Where(static interval => interval.Death.HasValue)
            .Select(static interval => interval.Death!.Value)
            .ToArray();
        if (deaths.Length < 2)
            return new(barcode.PointCount!.Value, null, false);

        int largestGapAfter = 0;
        float largestGap = float.NegativeInfinity;
        for (int i = 0; i < deaths.Length - 1; i++)
        {
            float gap = deaths[i + 1] - deaths[i];
            if (gap > largestGap)
            {
                largestGap = gap;
                largestGapAfter = i;
            }
        }
        return new(
            barcode.PointCount!.Value - (largestGapAfter + 1),
            (deaths[largestGapAfter] + deaths[largestGapAfter + 1]) / 2,
            false);
    }

    private static Materialization MaterializePopulation(
        IReadTransaction transaction,
        VectorIndexDefinition definition,
        PersistenceH0Options options,
        Stopwatch stopwatch)
    {
        var owners = new List<EntityRef>();
        var vectors = new List<float[]>();
        IEnumerable<EntityRef> candidates = definition.Target.OwnerKind switch
        {
            PropertyOwnerKind.Vertex => transaction.Query.Vertices().AsEnumerable().Select(EntityRef.From),
            PropertyOwnerKind.Edge => transaction.Query.Edges().AsEnumerable().Select(EntityRef.From),
            PropertyOwnerKind.Nexus => transaction.Query.Nexuses().AsEnumerable().Select(EntityRef.From),
            _ => [],
        };
        int scannedCandidates = 0;
        foreach (EntityRef owner in candidates)
        {
            if ((scannedCandidates++ & 63) == 0 && StopReason(options, stopwatch) is { } stop)
                return new(owners, vectors, stop);
            if (!MatchesScope(transaction, owner, definition.Target.Scope))
                continue;
            var vector = new float[definition.Dimensions];
            if (!transaction.TryGetVectorProperty(owner, definition.Target.PropertyKey, vector))
                continue;
            if (Array.Exists(vector, static value => !float.IsFinite(value)))
                throw new VectorException("H0 barcodeの入力vectorには有限値だけを指定できます。");
            if (owners.Count == options.MaxPoints)
                return new(owners, vectors, PersistenceH0TerminationReason.MaxPointsReached);
            owners.Add(owner);
            vectors.Add(vector);
        }
        return new(owners, vectors, null);
    }

    private static bool MatchesScope(IReadTransaction transaction, EntityRef owner, string? scope)
    {
        if (scope is null)
            return true;
        return owner.Kind switch
        {
            EntityKind.Vertex => transaction.GetVertexLabel(new VertexId(owner.Value)) == scope,
            EntityKind.Edge => transaction.GetEdgeType(new EdgeId(owner.Value)) == scope,
            EntityKind.Nexus => transaction.GetNexusType(new NexusId(owner.Value)) == scope,
            _ => false,
        };
    }

    private static EdgeBuild BuildCompleteEdges(
        IReadOnlyList<float[]> vectors,
        PersistenceH0Options options,
        Stopwatch stopwatch)
    {
        long edgeCount = checked((long)vectors.Count * (vectors.Count - 1) / 2);
        if (edgeCount > options.MaxEdges || edgeCount > int.MaxValue)
            return new([], PersistenceH0TerminationReason.MaxEdgesReached);
        var edges = new List<Edge>((int)edgeCount);
        for (int left = 0; left < vectors.Count; left++)
        {
            if ((left & 31) == 0 && StopReason(options, stopwatch) is { } stop)
                return new(edges, stop);
            for (int right = left + 1; right < vectors.Count; right++)
            {
                float distance = ValidateDistance(
                    VectorScorer.Euclidean(vectors[left], vectors[right]));
                edges.Add(new(left, right, distance));
            }
        }
        return new(edges, null);
    }

    private static EdgeBuild BuildSparseEdges(
        IReadTransaction transaction,
        string indexName,
        Materialization materialization,
        PersistenceH0Options options,
        Stopwatch stopwatch)
    {
        int pointCount = materialization.Owners.Count;
        if (pointCount <= 1)
            return new([], null);
        int k = Math.Min(pointCount, options.NeighborCount + 1);
        ReadOnlyMemory<float>[] queries = materialization.Vectors
            .Select(static vector => new ReadOnlyMemory<float>(vector))
            .ToArray();
        IReadOnlyList<VectorSearchCursor> cursors = transaction.KnnSearchBatch(indexName, queries, k);
        if (cursors.Count != pointCount)
        {
            foreach (VectorSearchCursor cursor in cursors)
                cursor.Dispose();
            throw new InvalidOperationException("Vector batch検索のcursor数が入力点数と一致しません。");
        }
        var ownerToPoint = new Dictionary<EntityRef, int>(pointCount);
        for (int i = 0; i < pointCount; i++)
            ownerToPoint.Add(materialization.Owners[i], i);
        int initialCapacity = (int)Math.Min(
            options.MaxEdges,
            Math.Min(int.MaxValue, (long)pointCount * Math.Min(k, 32)));
        var edges = new Dictionary<ulong, Edge>(initialCapacity);
        try
        {
            for (int point = 0; point < cursors.Count; point++)
            {
                if ((point & 31) == 0 && StopReason(options, stopwatch) is { } stop)
                    return new(edges.Values.ToArray(), stop);
                int accepted = 0;
                while (cursors[point].MoveNext())
                {
                    VectorSearchResult hit = cursors[point].Current;
                    if (hit.Owner == materialization.Owners[point])
                        continue;
                    if (!ownerToPoint.TryGetValue(hit.Owner, out int other))
                    {
                        throw new InvalidOperationException(
                            "Vector検索がindexのsnapshot可視な入力母集団外のownerを返しました。");
                    }
                    if (accepted++ >= options.NeighborCount)
                        break;
                    int left = Math.Min(point, other);
                    int right = Math.Max(point, other);
                    if (left == right)
                        continue;
                    ulong key = ((ulong)(uint)left << 32) | (uint)right;
                    float distance = ValidateDistance(-hit.Score);
                    if (edges.TryGetValue(key, out Edge existing))
                    {
                        if (distance < existing.Distance)
                            edges[key] = new(left, right, distance);
                    }
                    else
                    {
                        if (edges.Count == options.MaxEdges)
                            return new(edges.Values.ToArray(), PersistenceH0TerminationReason.MaxEdgesReached);
                        edges[key] = new(left, right, distance);
                    }
                }
            }
        }
        finally
        {
            foreach (VectorSearchCursor cursor in cursors)
                cursor.Dispose();
        }
        return new(edges.Values.ToArray(), null);
    }

    private static float ValidateDistance(float distance)
    {
        if (!float.IsFinite(distance) || distance < 0)
        {
            throw new VectorException(
                "H0 barcodeのEuclidean距離は0以上の有限値でなければなりません。");
        }
        return distance;
    }

    private static PersistenceH0TerminationReason? StopReason(
        PersistenceH0Options options,
        Stopwatch stopwatch)
    {
        if (options.CancellationToken.IsCancellationRequested)
            return PersistenceH0TerminationReason.Cancelled;
        if (options.TimeLimit != Timeout.InfiniteTimeSpan && stopwatch.Elapsed >= options.TimeLimit)
            return PersistenceH0TerminationReason.TimeBudget;
        return null;
    }

    private static PersistenceH0Result Incomplete(
        PersistenceH0Options options,
        PersistenceH0TerminationReason reason,
        int? pointCount = null,
        long distanceEvaluations = 0,
        int edgeCount = 0) =>
        new([], pointCount, pointCount, null, distanceEvaluations, edgeCount,
            options.Filtration, PersistenceH0ResultKind.Incomplete, reason, options.MaxScale);

    private static void Validate(PersistenceH0Options options)
    {
        if (!Enum.IsDefined(options.Filtration))
            throw new ArgumentOutOfRangeException(nameof(options), "Filtrationが未定義です。");
        if (options.NeighborCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "NeighborCountは正でなければなりません。");
        if (options.MaxPoints <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPointsは正でなければなりません。");
        if (options.MaxEdges <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxEdgesは正でなければなりません。");
        if (options.MaxDistanceEvaluations <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDistanceEvaluationsは正でなければなりません。");
        if (options.MaxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxResultsは正でなければなりません。");
        if (options.TimeLimit != Timeout.InfiniteTimeSpan && options.TimeLimit < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "TimeLimitは0以上またはInfiniteでなければなりません。");
        if (options.MaxScale is float maxScale && (!float.IsFinite(maxScale) || maxScale < 0))
            throw new ArgumentOutOfRangeException(nameof(options), "MaxScaleは0以上の有限値でなければなりません。");
    }

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        internal UnionFind(int count)
        {
            _parent = Enumerable.Range(0, count).ToArray();
            _rank = new byte[count];
            Components = count;
        }

        internal int Components { get; private set; }

        internal bool Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left == right)
                return false;
            if (_rank[left] < _rank[right])
                (left, right) = (right, left);
            _parent[right] = left;
            if (_rank[left] == _rank[right])
                _rank[left]++;
            Components--;
            return true;
        }

        private int Find(int value)
        {
            while (_parent[value] != value)
            {
                _parent[value] = _parent[_parent[value]];
                value = _parent[value];
            }
            return value;
        }
    }

    private readonly record struct Edge(int Left, int Right, float Distance);
    private sealed record Materialization(
        IReadOnlyList<EntityRef> Owners,
        IReadOnlyList<float[]> Vectors,
        PersistenceH0TerminationReason? StopReason);
    private sealed record EdgeBuild(
        IReadOnlyList<Edge> Edges,
        PersistenceH0TerminationReason? StopReason);
}
