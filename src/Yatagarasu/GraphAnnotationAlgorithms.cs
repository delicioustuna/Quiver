using System.Diagnostics;
using System.Numerics;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu;

/// <summary>組み込みグラフ注釈の評価順序。</summary>
internal enum GraphAnnotationPolicy
{
    /// <summary>到達済みVertexを先入れ先出しで展開する。</summary>
    BreadthFirst = 0,
    /// <summary>確定した最小ラベルから展開する。非負重みだけに使用できる。</summary>
    LabelSetting = 1,
    /// <summary>DAGをトポロジカル順に一度だけ展開する。</summary>
    AcyclicDynamicProgramming = 2,
    /// <summary>更新されたVertexを作業キューへ戻し、明示budgetまで反復する。</summary>
    BoundedWorklist = 3,
}

/// <summary>グラフ注釈評価が終了した理由。</summary>
internal enum GraphAnnotationTerminationReason
{
    /// <summary>入力全体を評価し、全結果を返した。</summary>
    Completed,
    /// <summary>起点Vertexがtransaction snapshotに存在しない。</summary>
    SourceNotFound,
    /// <summary>入力Vertex数の上限に達した。</summary>
    MaxVerticesReached,
    /// <summary>入力Edge数の上限に達した。</summary>
    MaxEdgesReached,
    /// <summary>結果数の上限に達した。</summary>
    MaxResultsReached,
    /// <summary>緩和回数の上限に達した。</summary>
    RelaxationBudgetExceeded,
    /// <summary>非冪等注釈の更新回数の上限に達した。</summary>
    AnnotationBudgetExceeded,
    /// <summary>時間上限に達した。</summary>
    TimeLimitReached,
    /// <summary>キャンセルが要求された。</summary>
    Cancelled,
    /// <summary>DAG専用policyへ循環グラフが渡された。</summary>
    CycleRejected,
    /// <summary>label-settingへ負辺が渡された。</summary>
    NegativeEdgeRejected,
    /// <summary>選択した組み込み注釈では使用できないpolicyが指定された。</summary>
    UnsupportedPolicyRejected,
    /// <summary>Edge値が有限値または組み込み注釈の値域ではない。</summary>
    InvalidEdgeValueRejected,
    /// <summary>有限入力の演算結果が表現範囲を超えた。</summary>
    NumericOverflow,
}

/// <summary>Edgeからtropical重みまたはViterbi確率を読み出す関数。</summary>
/// <remarks>例外は変換せず呼び出し元へ伝播する。</remarks>
/// <param name="transaction">評価対象の読み取りtransaction。</param>
/// <param name="edgeId">値を読み出すsnapshot可視Edge。</param>
/// <returns>tropicalでは有限重み、Viterbiでは0以上1以下の有限確率。</returns>
internal delegate double GraphEdgeValueSelector(IReadTransaction transaction, EdgeId edgeId);

/// <summary>組み込みグラフ注釈を評価するときの入力・作業・時間上限。</summary>
internal sealed class GraphAnnotationOptions
{
    /// <summary>対象にするEdge型。<c>null</c>は全型。</summary>
    public string? EdgeType { get; init; }

    /// <summary>snapshotからmaterializeするVertexの最大数。</summary>
    public int MaxVertices { get; init; } = 100_000;

    /// <summary>snapshotからmaterializeするEdgeの最大数。</summary>
    public int MaxEdges { get; init; } = 1_000_000;

    /// <summary>返す注釈の最大数。</summary>
    public int MaxResults { get; init; } = 100_000;

    /// <summary>Edge緩和の最大回数。</summary>
    public long MaxRelaxations { get; init; } = 10_000_000;

    /// <summary>非冪等な経路多重度を更新する最大回数。</summary>
    public long MaxAnnotationUpdates { get; init; } = 10_000_000;

    /// <summary>
    /// snapshot走査、policy検証、評価、結果materializationを含む協調的な時間上限。
    /// 無制限にする場合は <see cref="Timeout.InfiniteTimeSpan"/>。
    /// </summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>snapshot走査から結果materializationまで観測するキャンセル。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>一つのVertexに対応する組み込み注釈値。</summary>
/// <typeparam name="T">組み込み評価が返す値型。</typeparam>
/// <param name="VertexId">注釈対象のVertex。</param>
/// <param name="Value">組み込み評価で得た値。</param>
internal readonly record struct GraphAnnotation<T>(VertexId VertexId, T Value);

/// <summary>組み込みグラフ注釈の評価結果。</summary>
/// <typeparam name="T">Boolean、倍精度値、または経路多重度。</typeparam>
internal sealed class GraphAnnotationResult<T>
{
    internal GraphAnnotationResult(
        IReadOnlyList<GraphAnnotation<T>> annotations,
        int? totalAnnotationCount,
        GraphAnnotationPolicy policy,
        GraphAnnotationTerminationReason terminationReason,
        int vertexCount,
        int edgeCount,
        long relaxationCount,
        long annotationUpdateCount,
        int queuePeak)
    {
        Annotations = annotations;
        TotalAnnotationCount = totalAnnotationCount;
        Policy = policy;
        TerminationReason = terminationReason;
        VertexCount = vertexCount;
        EdgeCount = edgeCount;
        RelaxationCount = relaxationCount;
        AnnotationUpdateCount = annotationUpdateCount;
        QueuePeak = queuePeak;
    }

    /// <summary>Vertex ID昇順の注釈。未収束時は空で、結果上限時は決定的prefix。</summary>
    public IReadOnlyList<GraphAnnotation<T>> Annotations { get; }

    /// <summary>評価完了後に判明した注釈総数。評価自体を完了できなければ<c>null</c>。</summary>
    public int? TotalAnnotationCount { get; }

    /// <summary>呼び出し元が明示した評価policy。</summary>
    public GraphAnnotationPolicy Policy { get; }

    /// <summary>入力全体の評価と結果materializationを完了したか。</summary>
    public bool IsComplete => TerminationReason == GraphAnnotationTerminationReason.Completed;

    /// <summary>要求したsnapshot全体について厳密な注釈を返したか。</summary>
    public bool IsExact => IsComplete;

    /// <summary>評価が終了した理由。</summary>
    public GraphAnnotationTerminationReason TerminationReason { get; }

    /// <summary>上限内でmaterializeしたVertex数。</summary>
    public int VertexCount { get; }

    /// <summary>上限内でmaterializeしたEdge数。</summary>
    public int EdgeCount { get; }

    /// <summary>実行したEdge緩和回数。</summary>
    public long RelaxationCount { get; }

    /// <summary>実行した非冪等注釈の更新回数。</summary>
    public long AnnotationUpdateCount { get; }

    /// <summary>評価中の作業キュー最大要素数。トポロジカル評価では0。</summary>
    public int QueuePeak { get; }
}

/// <summary>transaction snapshot上で固定の組み込みグラフ注釈を評価する。</summary>
internal static class GraphAnnotationAlgorithms
{
    /// <summary>
    /// Boolean注釈をBFSで評価する。
    /// 計算量はO(V+E)、追加空間はO(V+E)である。
    /// </summary>
    /// <param name="transaction">評価対象snapshotを保持する読み取りtransaction。</param>
    /// <param name="source">到達判定の起点Vertex。</param>
    /// <param name="policy"><see cref="GraphAnnotationPolicy.BreadthFirst"/>だけを受け付ける。</param>
    /// <param name="options">入力、結果、作業量、時間の上限。</param>
    /// <returns>到達可能なVertexのBoolean注釈。</returns>
    public static GraphAnnotationResult<bool> EvaluateReachability(
        this IReadTransaction transaction,
        VertexId source,
        GraphAnnotationPolicy policy,
        GraphAnnotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!Enum.IsDefined(policy) || policy != GraphAnnotationPolicy.BreadthFirst)
            return Rejected<bool>(policy, GraphAnnotationTerminationReason.UnsupportedPolicyRejected);
        EvaluationSetup setup = Prepare(transaction, source, policy, options, null, ValueDomain.None);
        if (!setup.Accepted) return setup.Failure<bool>();
        return EvaluateBoolean(setup);
    }

    /// <summary>
    /// tropical注釈の最小加算コストを評価する。
    /// label-settingはO((V+E) log V)、DAGはO(V+E)、bounded worklistはbudgetで制限される。
    /// </summary>
    /// <param name="transaction">評価対象snapshotを保持する読み取りtransaction。</param>
    /// <param name="source">コスト0とする起点Vertex。</param>
    /// <param name="policy">非負辺用label-setting、DAG用DP、またはbounded worklist。</param>
    /// <param name="valueSelector">各snapshot可視Edgeの有限重みを返す関数。</param>
    /// <param name="options">入力、結果、作業量、時間の上限。</param>
    /// <returns>到達可能なVertexと最小加算コスト。</returns>
    /// <example>
    /// <code>
    /// GraphAnnotationResult&lt;double&gt; costs = read.EvaluateTropical(
    ///     source,
    ///     GraphAnnotationPolicy.LabelSetting,
    ///     (transaction, edge) =&gt; transaction.GetProperty(edge, "cost").DoubleValue,
    ///     new GraphAnnotationOptions { EdgeType = "Route" });
    /// </code>
    /// </example>
    public static GraphAnnotationResult<double> EvaluateTropical(
        this IReadTransaction transaction,
        VertexId source,
        GraphAnnotationPolicy policy,
        GraphEdgeValueSelector valueSelector,
        GraphAnnotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(valueSelector);
        if (!Enum.IsDefined(policy) || policy == GraphAnnotationPolicy.BreadthFirst)
            return Rejected<double>(policy, GraphAnnotationTerminationReason.UnsupportedPolicyRejected);
        EvaluationSetup setup = Prepare(transaction, source, policy, options, valueSelector, ValueDomain.Tropical);
        if (!setup.Accepted) return setup.Failure<double>();
        return EvaluateTropicalCore(setup);
    }

    /// <summary>
    /// DAG上のViterbi注釈をトポロジカルDPで評価する。
    /// Edge値は0以上1以下の有限確率で、計算量はO(V+E)である。
    /// </summary>
    /// <param name="transaction">評価対象snapshotを保持する読み取りtransaction。</param>
    /// <param name="source">確率1とする起点Vertex。</param>
    /// <param name="policy"><see cref="GraphAnnotationPolicy.AcyclicDynamicProgramming"/>だけを受け付ける。</param>
    /// <param name="valueSelector">各snapshot可視Edgeの0以上1以下の有限確率を返す関数。</param>
    /// <param name="options">入力、結果、作業量、時間の上限。</param>
    /// <returns>到達可能なVertexと最大積確率。</returns>
    public static GraphAnnotationResult<double> EvaluateViterbi(
        this IReadTransaction transaction,
        VertexId source,
        GraphAnnotationPolicy policy,
        GraphEdgeValueSelector valueSelector,
        GraphAnnotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(valueSelector);
        if (!Enum.IsDefined(policy) || policy != GraphAnnotationPolicy.AcyclicDynamicProgramming)
            return Rejected<double>(policy, GraphAnnotationTerminationReason.UnsupportedPolicyRejected);
        EvaluationSetup setup = Prepare(transaction, source, policy, options, valueSelector, ValueDomain.Probability);
        if (!setup.Accepted) return setup.Failure<double>();
        return EvaluateViterbiCore(setup);
    }

    /// <summary>
    /// 起点から各Vertexまでのwalk多重度を非負整数注釈として評価する。
    /// DAGでは有限path数へ収束し、循環による成長はannotation budgetまたは数値上限で停止する。
    /// </summary>
    /// <param name="transaction">評価対象snapshotを保持する読み取りtransaction。</param>
    /// <param name="source">空pathを1とする起点Vertex。</param>
    /// <param name="policy"><see cref="GraphAnnotationPolicy.BoundedWorklist"/>だけを受け付ける。</param>
    /// <param name="options">入力、結果、作業量、時間の上限。</param>
    /// <returns>到達可能なVertexとwalk多重度。循環成長時は空結果と明示終了理由。</returns>
    public static GraphAnnotationResult<long> EvaluatePathMultiplicity(
        this IReadTransaction transaction,
        VertexId source,
        GraphAnnotationPolicy policy,
        GraphAnnotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!Enum.IsDefined(policy) || policy != GraphAnnotationPolicy.BoundedWorklist)
            return Rejected<long>(policy, GraphAnnotationTerminationReason.UnsupportedPolicyRejected);
        EvaluationSetup setup = Prepare(transaction, source, policy, options, null, ValueDomain.None);
        if (!setup.Accepted) return setup.Failure<long>();
        return EvaluateMultiplicity(setup);
    }

    private static EvaluationSetup Prepare(
        IReadTransaction transaction,
        VertexId source,
        GraphAnnotationPolicy policy,
        GraphAnnotationOptions? options,
        GraphEdgeValueSelector? selector,
        ValueDomain domain)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        options ??= new GraphAnnotationOptions();
        ValidateOptions(options);
        var control = new ExecutionControl(options);
        var vertices = new List<VertexId>(Math.Min(options.MaxVertices, 4096));
        foreach (VertexId vertex in transaction.Query.Vertices().AsEnumerable())
        {
            if (control.TryStop(out var stop)) return EvaluationSetup.Stopped(policy, options, control, stop, vertices.Count, 0);
            if (vertices.Count >= options.MaxVertices)
                return EvaluationSetup.Stopped(policy, options, control, GraphAnnotationTerminationReason.MaxVerticesReached, vertices.Count, 0);
            vertices.Add(vertex);
        }
        vertices.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        if (control.TryStop(out var vertexSortStop))
            return EvaluationSetup.Stopped(policy, options, control, vertexSortStop, vertices.Count, 0);
        var indices = new Dictionary<VertexId, int>(vertices.Count);
        for (int i = 0; i < vertices.Count; i++)
        {
            if (control.TryStop(out var indexStop))
                return EvaluationSetup.Stopped(policy, options, control, indexStop, vertices.Count, 0);
            indices.Add(vertices[i], i);
        }
        if (!indices.TryGetValue(source, out int sourceIndex))
            return EvaluationSetup.Stopped(policy, options, control, GraphAnnotationTerminationReason.SourceNotFound, vertices.Count, 0);

        var outgoing = new List<Arc>[vertices.Count];
        for (int i = 0; i < outgoing.Length; i++) outgoing[i] = [];
        int edgeCount = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            EdgeEnumerator edges = transaction.EnumerateEdges(vertices[i], Direction.Both, options.EdgeType);
            try
            {
                while (edges.MoveNext())
                {
                    if (control.TryStop(out var stop)) return EvaluationSetup.Stopped(policy, options, control, stop, vertices.Count, edgeCount);
                    EdgeReadHandle edge = edges.Current;
                    if (edge.Source != vertices[i]) continue;
                    if (edgeCount >= options.MaxEdges)
                        return EvaluationSetup.Stopped(policy, options, control, GraphAnnotationTerminationReason.MaxEdgesReached, vertices.Count, edgeCount);
                    if (!indices.TryGetValue(edge.Target, out int target)) continue;
                    double value = selector is null ? 0 : selector(transaction, edge.Id);
                    if (!double.IsFinite(value) || (domain == ValueDomain.Probability && (value < 0 || value > 1)))
                        return EvaluationSetup.Stopped(policy, options, control, GraphAnnotationTerminationReason.InvalidEdgeValueRejected, vertices.Count, edgeCount + 1);
                    outgoing[i].Add(new Arc(target, edge.Id, value));
                    edgeCount++;
                }
            }
            finally
            {
                edges.Dispose();
            }
            outgoing[i].Sort(static (left, right) => left.EdgeId.Value.CompareTo(right.EdgeId.Value));
            if (control.TryStop(out var edgeSortStop))
                return EvaluationSetup.Stopped(policy, options, control, edgeSortStop, vertices.Count, edgeCount);
        }

        if (policy == GraphAnnotationPolicy.LabelSetting && outgoing.Any(static list => list.Any(static edge => edge.Value < 0)))
            return EvaluationSetup.Stopped(policy, options, control, GraphAnnotationTerminationReason.NegativeEdgeRejected, vertices.Count, edgeCount);

        int[]? topologicalOrder = null;
        if (policy == GraphAnnotationPolicy.AcyclicDynamicProgramming)
        {
            (topologicalOrder, GraphAnnotationTerminationReason? stop) = TopologicalOrder(outgoing, vertices, control);
            if (stop is not null)
                return EvaluationSetup.Stopped(policy, options, control, stop.Value, vertices.Count, edgeCount);
            if (topologicalOrder is null)
                return EvaluationSetup.Stopped(policy, options, control, GraphAnnotationTerminationReason.CycleRejected, vertices.Count, edgeCount);
        }
        return new(true, policy, options, control, vertices.ToArray(), outgoing, sourceIndex, edgeCount, topologicalOrder, null);
    }

    private static GraphAnnotationResult<bool> EvaluateBoolean(EvaluationSetup setup)
    {
        var reached = new bool[setup.Vertices.Length];
        var queue = new Queue<int>();
        reached[setup.SourceIndex] = true;
        queue.Enqueue(setup.SourceIndex);
        long relaxations = 0;
        int peak = 1;
        while (queue.TryDequeue(out int vertex))
        {
            foreach (Arc edge in setup.Outgoing[vertex])
            {
                if (TryStopEvaluation<bool>(setup, relaxations, 0, peak, out var stopped)) return stopped;
                if (relaxations >= setup.Options.MaxRelaxations)
                    return Failure<bool>(setup, GraphAnnotationTerminationReason.RelaxationBudgetExceeded, relaxations, 0, peak);
                relaxations++;
                if (reached[edge.Target]) continue;
                reached[edge.Target] = true;
                queue.Enqueue(edge.Target);
                peak = Math.Max(peak, queue.Count);
            }
        }
        return Materialize(setup, reached, static (value, _) => value, relaxations, 0, peak);
    }

    private static GraphAnnotationResult<double> EvaluateTropicalCore(EvaluationSetup setup)
    {
        double[] values = Enumerable.Repeat(double.PositiveInfinity, setup.Vertices.Length).ToArray();
        values[setup.SourceIndex] = 0;
        long relaxations = 0;
        int peak = 0;
        if (setup.Policy == GraphAnnotationPolicy.AcyclicDynamicProgramming)
        {
            foreach (int vertex in setup.TopologicalOrder!)
            {
                if (double.IsPositiveInfinity(values[vertex])) continue;
                foreach (Arc edge in setup.Outgoing[vertex])
                {
                    if (TryStopEvaluation<double>(setup, relaxations, 0, 0, out var stopped)) return stopped;
                    if (relaxations >= setup.Options.MaxRelaxations)
                        return Failure<double>(setup, GraphAnnotationTerminationReason.RelaxationBudgetExceeded, relaxations, 0, 0);
                    relaxations++;
                    double candidate = values[vertex] + edge.Value;
                    if (!double.IsFinite(candidate)) return Failure<double>(setup, GraphAnnotationTerminationReason.NumericOverflow, relaxations, 0, 0);
                    if (candidate < values[edge.Target]) values[edge.Target] = candidate;
                }
            }
        }
        else if (setup.Policy == GraphAnnotationPolicy.LabelSetting)
        {
            var queue = new PriorityQueue<int, (double Distance, long Vertex)>();
            queue.Enqueue(setup.SourceIndex, (0, setup.Vertices[setup.SourceIndex].Value));
            peak = 1;
            while (queue.TryDequeue(out int vertex, out var priority))
            {
                if (priority.Distance != values[vertex]) continue;
                foreach (Arc edge in setup.Outgoing[vertex])
                {
                    if (TryStopEvaluation<double>(setup, relaxations, 0, peak, out var stopped)) return stopped;
                    if (relaxations >= setup.Options.MaxRelaxations)
                        return Failure<double>(setup, GraphAnnotationTerminationReason.RelaxationBudgetExceeded, relaxations, 0, peak);
                    relaxations++;
                    double candidate = priority.Distance + edge.Value;
                    if (!double.IsFinite(candidate)) return Failure<double>(setup, GraphAnnotationTerminationReason.NumericOverflow, relaxations, 0, peak);
                    if (candidate >= values[edge.Target]) continue;
                    values[edge.Target] = candidate;
                    queue.Enqueue(edge.Target, (candidate, setup.Vertices[edge.Target].Value));
                    peak = Math.Max(peak, queue.Count);
                }
            }
        }
        else
        {
            var queue = new Queue<int>();
            var queued = new bool[setup.Vertices.Length];
            queue.Enqueue(setup.SourceIndex);
            queued[setup.SourceIndex] = true;
            peak = 1;
            while (queue.TryDequeue(out int vertex))
            {
                queued[vertex] = false;
                foreach (Arc edge in setup.Outgoing[vertex])
                {
                    if (TryStopEvaluation<double>(setup, relaxations, 0, peak, out var stopped)) return stopped;
                    if (relaxations >= setup.Options.MaxRelaxations)
                        return Failure<double>(setup, GraphAnnotationTerminationReason.RelaxationBudgetExceeded, relaxations, 0, peak);
                    relaxations++;
                    double candidate = values[vertex] + edge.Value;
                    if (!double.IsFinite(candidate)) return Failure<double>(setup, GraphAnnotationTerminationReason.NumericOverflow, relaxations, 0, peak);
                    if (candidate >= values[edge.Target]) continue;
                    values[edge.Target] = candidate;
                    if (queued[edge.Target]) continue;
                    queue.Enqueue(edge.Target);
                    queued[edge.Target] = true;
                    peak = Math.Max(peak, queue.Count);
                }
            }
        }
        return Materialize(setup, values, static (value, _) => !double.IsPositiveInfinity(value), relaxations, 0, peak);
    }

    private static GraphAnnotationResult<double> EvaluateViterbiCore(EvaluationSetup setup)
    {
        var values = new double[setup.Vertices.Length];
        var reached = new bool[setup.Vertices.Length];
        values[setup.SourceIndex] = 1;
        reached[setup.SourceIndex] = true;
        long relaxations = 0;
        foreach (int vertex in setup.TopologicalOrder!)
        {
            if (!reached[vertex]) continue;
            foreach (Arc edge in setup.Outgoing[vertex])
            {
                if (TryStopEvaluation<double>(setup, relaxations, 0, 0, out var stopped)) return stopped;
                if (relaxations >= setup.Options.MaxRelaxations)
                    return Failure<double>(setup, GraphAnnotationTerminationReason.RelaxationBudgetExceeded, relaxations, 0, 0);
                relaxations++;
                reached[edge.Target] = true;
                values[edge.Target] = Math.Max(values[edge.Target], values[vertex] * edge.Value);
            }
        }
        return Materialize(setup, values, (_, index) => reached[index], relaxations, 0, 0);
    }

    private static GraphAnnotationResult<long> EvaluateMultiplicity(EvaluationSetup setup)
    {
        var values = new BigInteger[setup.Vertices.Length];
        var deltas = new BigInteger[setup.Vertices.Length];
        var queued = new bool[setup.Vertices.Length];
        var queue = new Queue<int>();
        values[setup.SourceIndex] = 1;
        deltas[setup.SourceIndex] = 1;
        queued[setup.SourceIndex] = true;
        queue.Enqueue(setup.SourceIndex);
        long relaxations = 0;
        long updates = 0;
        int peak = 1;
        while (queue.TryDequeue(out int vertex))
        {
            queued[vertex] = false;
            BigInteger delta = deltas[vertex];
            deltas[vertex] = 0;
            foreach (Arc edge in setup.Outgoing[vertex])
            {
                if (TryStopEvaluation<long>(setup, relaxations, updates, peak, out var stopped)) return stopped;
                if (relaxations >= setup.Options.MaxRelaxations)
                    return Failure<long>(setup, GraphAnnotationTerminationReason.RelaxationBudgetExceeded, relaxations, updates, peak);
                if (updates >= setup.Options.MaxAnnotationUpdates)
                    return Failure<long>(setup, GraphAnnotationTerminationReason.AnnotationBudgetExceeded, relaxations, updates, peak);
                relaxations++;
                updates++;
                values[edge.Target] += delta;
                deltas[edge.Target] += delta;
                if (queued[edge.Target]) continue;
                queued[edge.Target] = true;
                queue.Enqueue(edge.Target);
                peak = Math.Max(peak, queue.Count);
            }
        }
        if (values.Any(static value => value > long.MaxValue))
            return Failure<long>(setup, GraphAnnotationTerminationReason.NumericOverflow, relaxations, updates, peak);
        long[] materialized = values.Select(static value => (long)value).ToArray();
        return Materialize(setup, materialized, static (value, _) => value != 0, relaxations, updates, peak);
    }

    private static GraphAnnotationResult<T> Materialize<T>(
        EvaluationSetup setup,
        T[] values,
        Func<T, int, bool> include,
        long relaxations,
        long updates,
        int peak)
    {
        int total = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (setup.Control.TryStop(out var stop)) return Failure<T>(setup, stop, relaxations, updates, peak);
            if (include(values[i], i)) total++;
        }
        var result = new List<GraphAnnotation<T>>(Math.Min(total, setup.Options.MaxResults));
        for (int i = 0; i < values.Length && result.Count < setup.Options.MaxResults; i++)
        {
            if (setup.Control.TryStop(out var stop)) return Failure<T>(setup, stop, relaxations, updates, peak);
            if (include(values[i], i)) result.Add(new(setup.Vertices[i], values[i]));
        }
        GraphAnnotationTerminationReason reason = result.Count < total
            ? GraphAnnotationTerminationReason.MaxResultsReached
            : GraphAnnotationTerminationReason.Completed;
        return new(result, total, setup.Policy, reason, setup.Vertices.Length, setup.EdgeCount, relaxations, updates, peak);
    }

    private static bool TryStopEvaluation<T>(
        EvaluationSetup setup,
        long relaxations,
        long updates,
        int peak,
        out GraphAnnotationResult<T> result)
    {
        if (setup.Control.TryStop(out var stop))
        {
            result = Failure<T>(setup, stop, relaxations, updates, peak);
            return true;
        }
        result = null!;
        return false;
    }

    private static GraphAnnotationResult<T> Failure<T>(
        EvaluationSetup setup,
        GraphAnnotationTerminationReason reason,
        long relaxations,
        long updates,
        int peak)
        => new([], null, setup.Policy, reason, setup.Vertices.Length, setup.EdgeCount, relaxations, updates, peak);

    private static GraphAnnotationResult<T> Rejected<T>(GraphAnnotationPolicy policy, GraphAnnotationTerminationReason reason)
        => new([], null, policy, reason, 0, 0, 0, 0, 0);

    private static (int[]? Order, GraphAnnotationTerminationReason? Stop) TopologicalOrder(
        List<Arc>[] outgoing,
        IReadOnlyList<VertexId> vertices,
        ExecutionControl control)
    {
        var indegree = new int[outgoing.Length];
        foreach (List<Arc> edges in outgoing)
            foreach (Arc edge in edges)
            {
                if (control.TryStop(out var stop)) return (null, stop);
                indegree[edge.Target]++;
            }
        var ready = new PriorityQueue<int, long>();
        for (int i = 0; i < indegree.Length; i++) if (indegree[i] == 0) ready.Enqueue(i, vertices[i].Value);
        var order = new int[outgoing.Length];
        int count = 0;
        while (ready.TryDequeue(out int vertex, out _))
        {
            if (control.TryStop(out var stop)) return (null, stop);
            order[count++] = vertex;
            foreach (Arc edge in outgoing[vertex])
                if (--indegree[edge.Target] == 0) ready.Enqueue(edge.Target, vertices[edge.Target].Value);
        }
        return count == order.Length ? (order, null) : (null, null);
    }

    private static void ValidateOptions(GraphAnnotationOptions options)
    {
        if (options.MaxVertices < 0 || options.MaxEdges < 0 || options.MaxResults < 0
            || options.MaxRelaxations < 0 || options.MaxAnnotationUpdates < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "件数と作業量の上限は0以上でなければなりません。");
        if (options.TimeLimit < TimeSpan.Zero && options.TimeLimit != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "TimeLimitは0以上または無制限でなければなりません。");
    }

    private enum ValueDomain { None, Tropical, Probability }
    private readonly record struct Arc(int Target, EdgeId EdgeId, double Value);

    private sealed class ExecutionControl(GraphAnnotationOptions options)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();

        internal bool TryStop(out GraphAnnotationTerminationReason reason)
        {
            if (options.CancellationToken.IsCancellationRequested)
            {
                reason = GraphAnnotationTerminationReason.Cancelled;
                return true;
            }
            if (options.TimeLimit != Timeout.InfiniteTimeSpan && _elapsed.Elapsed >= options.TimeLimit)
            {
                reason = GraphAnnotationTerminationReason.TimeLimitReached;
                return true;
            }
            reason = default;
            return false;
        }
    }

    private sealed record EvaluationSetup(
        bool Accepted,
        GraphAnnotationPolicy Policy,
        GraphAnnotationOptions Options,
        ExecutionControl Control,
        VertexId[] Vertices,
        List<Arc>[] Outgoing,
        int SourceIndex,
        int EdgeCount,
        int[]? TopologicalOrder,
        GraphAnnotationTerminationReason? FailureReason)
    {
        internal static EvaluationSetup Stopped(
            GraphAnnotationPolicy policy,
            GraphAnnotationOptions options,
            ExecutionControl control,
            GraphAnnotationTerminationReason reason,
            int vertexCount,
            int edgeCount)
            => new(false, policy, options, control, new VertexId[vertexCount], [], -1, edgeCount, null, reason);

        internal GraphAnnotationResult<T> Failure<T>()
            => new([], null, Policy, FailureReason!.Value, Vertices.Length, EdgeCount, 0, 0, 0);
    }
}
