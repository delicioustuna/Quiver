using System.Diagnostics;
using Yatagarasu.Core;

namespace Yatagarasu.Query.Optimizer;

internal readonly record struct CyclicTriangleJoinPair(VertexId Left, VertexId Right);

internal readonly record struct CyclicTriangleJoinRow(VertexId First, VertexId Second, VertexId Third);

internal enum CyclicTriangleJoinStrategy
{
    Empty,
    Materializing,
    TransientColumns,
}

internal enum CyclicTriangleJoinRouteReason
{
    EmptyRelation,
    InputLimit,
    AnalysisIncomplete,
    DenseCyclic,
    DuplicatePair,
    IntermediateTooSmall,
    AmplificationTooLow,
    ComparisonBaseline,
}

internal enum CyclicTriangleJoinTerminationReason
{
    Completed,
    MaxResultsReached,
    WorkBudgetReached,
    TimeBudgetReached,
    InputLimitReached,
    IntermediateLimitReached,
}

internal sealed class CyclicTriangleJoinOptions
{
    public int MaxResults { get; init; } = int.MaxValue;
    public long MaxInputRows { get; init; } = 1_000_000;
    // MaxWork counts join comparisons and materialized intermediate rows. Analysis, column build, and sort
    // are covered by the input limit, elapsed-time limit, and cancellation checks instead.
    public long MaxWork { get; init; } = long.MaxValue;
    public int MaxMaterializedIntermediateRows { get; init; } = 4_000_000;
    public TimeSpan TimeLimit { get; init; } = Timeout.InfiniteTimeSpan;
    public CancellationToken CancellationToken { get; init; }
}

internal sealed class CyclicTriangleJoinResult
{
    internal CyclicTriangleJoinResult(
        CyclicTriangleJoinRow[] rows,
        CyclicTriangleJoinStrategy strategy,
        CyclicTriangleJoinRouteReason routeReason,
        CyclicTriangleJoinTerminationReason terminationReason,
        long estimatedIntermediateRows,
        long work,
        int peakWorkingRows)
    {
        Rows = rows;
        Strategy = strategy;
        RouteReason = routeReason;
        TerminationReason = terminationReason;
        EstimatedIntermediateRows = estimatedIntermediateRows;
        Work = work;
        PeakWorkingRows = peakWorkingRows;
    }

    public CyclicTriangleJoinRow[] Rows { get; }
    public CyclicTriangleJoinStrategy Strategy { get; }
    public CyclicTriangleJoinRouteReason RouteReason { get; }
    public CyclicTriangleJoinTerminationReason TerminationReason { get; }
    public long EstimatedIntermediateRows { get; }
    public long Work { get; }
    public int PeakWorkingRows { get; }
    public bool IsComplete => TerminationReason == CyclicTriangleJoinTerminationReason.Completed;
}

/// <summary>
/// Three directed binary relations R(a,b), S(b,c), T(c,a) are the only shape accepted here.
/// Match grammar remains unchanged; callers first extract the three relations through the existing Match path.
/// </summary>
internal static class CyclicTriangleJoinOptimizer
{
    internal const long MinimumMaterializedIntermediateRows = 32_768;
    internal const long MinimumIntermediateAmplification = 4;

    public static CyclicTriangleJoinResult Execute(
        IReadOnlyList<CyclicTriangleJoinPair> firstToSecond,
        IReadOnlyList<CyclicTriangleJoinPair> secondToThird,
        IReadOnlyList<CyclicTriangleJoinPair> thirdToFirst,
        CyclicTriangleJoinOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(firstToSecond);
        ArgumentNullException.ThrowIfNull(secondToThird);
        ArgumentNullException.ThrowIfNull(thirdToFirst);
        options ??= new CyclicTriangleJoinOptions();
        ValidateOptions(options);
        options.CancellationToken.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        if (!ObserveRuntime(options, elapsed, out var stop))
            return Result([], CyclicTriangleJoinStrategy.Empty, CyclicTriangleJoinRouteReason.AnalysisIncomplete,
                stop, 0, 0, 0);

        long inputRows = TotalInputRows(firstToSecond, secondToThird, thirdToFirst);
        if (inputRows > options.MaxInputRows)
            return Result([], CyclicTriangleJoinStrategy.Empty, CyclicTriangleJoinRouteReason.InputLimit,
                CyclicTriangleJoinTerminationReason.InputLimitReached, 0, 0, 0);

        if (firstToSecond.Count == 0 || secondToThird.Count == 0 || thirdToFirst.Count == 0)
        {
            return Result([], CyclicTriangleJoinStrategy.Empty, CyclicTriangleJoinRouteReason.EmptyRelation,
                CyclicTriangleJoinTerminationReason.Completed, 0, 0, 0);
        }

        Analysis analysis = Analyze(firstToSecond, secondToThird, thirdToFirst, options, elapsed);
        if (analysis.TerminationReason != CyclicTriangleJoinTerminationReason.Completed)
            return Result([], CyclicTriangleJoinStrategy.Empty, CyclicTriangleJoinRouteReason.AnalysisIncomplete,
                analysis.TerminationReason, analysis.EstimatedIntermediateRows, 0, 0);
        return analysis.RouteReason == CyclicTriangleJoinRouteReason.DenseCyclic
            ? ExecuteTransient(firstToSecond, secondToThird, thirdToFirst,
                analysis.EstimatedIntermediateRows, options, elapsed)
            : ExecuteMaterializing(firstToSecond, secondToThird, thirdToFirst,
                analysis.EstimatedIntermediateRows, analysis.RouteReason, options, elapsed);
    }

    internal static CyclicTriangleJoinResult ExecuteMaterializingForComparison(
        IReadOnlyList<CyclicTriangleJoinPair> firstToSecond,
        IReadOnlyList<CyclicTriangleJoinPair> secondToThird,
        IReadOnlyList<CyclicTriangleJoinPair> thirdToFirst,
        CyclicTriangleJoinOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(firstToSecond);
        ArgumentNullException.ThrowIfNull(secondToThird);
        ArgumentNullException.ThrowIfNull(thirdToFirst);
        options ??= new CyclicTriangleJoinOptions();
        ValidateOptions(options);
        options.CancellationToken.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        if (!ObserveRuntime(options, elapsed, out var stop))
            return Result([], CyclicTriangleJoinStrategy.Empty, CyclicTriangleJoinRouteReason.AnalysisIncomplete,
                stop, 0, 0, 0);

        long inputRows = TotalInputRows(firstToSecond, secondToThird, thirdToFirst);
        if (inputRows > options.MaxInputRows)
            return Result([], CyclicTriangleJoinStrategy.Empty, CyclicTriangleJoinRouteReason.InputLimit,
                CyclicTriangleJoinTerminationReason.InputLimitReached, 0, 0, 0);

        if (firstToSecond.Count == 0 || secondToThird.Count == 0 || thirdToFirst.Count == 0)
        {
            return Result([], CyclicTriangleJoinStrategy.Empty, CyclicTriangleJoinRouteReason.EmptyRelation,
                CyclicTriangleJoinTerminationReason.Completed, 0, 0, 0);
        }

        long estimate = EstimateIntermediate(firstToSecond, secondToThird, options, elapsed, out stop);
        if (stop != CyclicTriangleJoinTerminationReason.Completed)
            return Result([], CyclicTriangleJoinStrategy.Empty, CyclicTriangleJoinRouteReason.AnalysisIncomplete,
                stop, estimate, 0, 0);
        return ExecuteMaterializing(firstToSecond, secondToThird, thirdToFirst, estimate,
            CyclicTriangleJoinRouteReason.ComparisonBaseline, options, elapsed);
    }

    private static Analysis Analyze(
        IReadOnlyList<CyclicTriangleJoinPair> firstToSecond,
        IReadOnlyList<CyclicTriangleJoinPair> secondToThird,
        IReadOnlyList<CyclicTriangleJoinPair> thirdToFirst,
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed)
    {
        bool duplicate = ContainsDuplicate(firstToSecond, options, elapsed, out var stop);
        if (stop != CyclicTriangleJoinTerminationReason.Completed)
            return new(0, CyclicTriangleJoinRouteReason.AnalysisIncomplete, stop);
        if (!duplicate)
        {
            duplicate = ContainsDuplicate(secondToThird, options, elapsed, out stop);
            if (stop != CyclicTriangleJoinTerminationReason.Completed)
                return new(0, CyclicTriangleJoinRouteReason.AnalysisIncomplete, stop);
        }
        if (!duplicate)
        {
            duplicate = ContainsDuplicate(thirdToFirst, options, elapsed, out stop);
            if (stop != CyclicTriangleJoinTerminationReason.Completed)
                return new(0, CyclicTriangleJoinRouteReason.AnalysisIncomplete, stop);
        }
        if (duplicate)
        {
            long duplicateEstimate = EstimateIntermediate(firstToSecond, secondToThird, options, elapsed, out stop);
            return new(duplicateEstimate,
                stop == CyclicTriangleJoinTerminationReason.Completed
                    ? CyclicTriangleJoinRouteReason.DuplicatePair
                    : CyclicTriangleJoinRouteReason.AnalysisIncomplete,
                stop);
        }

        long estimate = EstimateIntermediate(firstToSecond, secondToThird, options, elapsed, out stop);
        if (stop != CyclicTriangleJoinTerminationReason.Completed)
            return new(estimate, CyclicTriangleJoinRouteReason.AnalysisIncomplete, stop);
        if (estimate < MinimumMaterializedIntermediateRows)
            return new(estimate, CyclicTriangleJoinRouteReason.IntermediateTooSmall,
                CyclicTriangleJoinTerminationReason.Completed);

        long inputRows = SaturatingAdd(firstToSecond.Count, SaturatingAdd(secondToThird.Count, thirdToFirst.Count));
        if (estimate < SaturatingMultiply(inputRows, MinimumIntermediateAmplification))
            return new(estimate, CyclicTriangleJoinRouteReason.AmplificationTooLow,
                CyclicTriangleJoinTerminationReason.Completed);

        return new(estimate, CyclicTriangleJoinRouteReason.DenseCyclic,
            CyclicTriangleJoinTerminationReason.Completed);
    }

    private static CyclicTriangleJoinResult ExecuteTransient(
        IReadOnlyList<CyclicTriangleJoinPair> firstToSecond,
        IReadOnlyList<CyclicTriangleJoinPair> secondToThird,
        IReadOnlyList<CyclicTriangleJoinPair> thirdToFirst,
        long estimatedIntermediateRows,
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed)
    {
        if (!ObserveRuntime(options, elapsed, out var stop))
            return Result([], CyclicTriangleJoinStrategy.TransientColumns, CyclicTriangleJoinRouteReason.DenseCyclic,
                stop, estimatedIntermediateRows, 0, 0);

        CyclicTriangleJoinPair[] root = firstToSecond.ToArray();
        Array.Sort(root, ComparePair);
        Dictionary<VertexId, VertexId[]> thirdBySecond = BuildColumns(secondToThird, reverse: false, options, elapsed, out stop);
        if (stop != CyclicTriangleJoinTerminationReason.Completed)
            return Result([], CyclicTriangleJoinStrategy.TransientColumns, CyclicTriangleJoinRouteReason.DenseCyclic,
                stop, estimatedIntermediateRows, 0, 0);
        Dictionary<VertexId, VertexId[]> thirdByFirst = BuildColumns(thirdToFirst, reverse: true, options, elapsed, out stop);
        if (stop != CyclicTriangleJoinTerminationReason.Completed)
            return Result([], CyclicTriangleJoinStrategy.TransientColumns, CyclicTriangleJoinRouteReason.DenseCyclic,
                stop, estimatedIntermediateRows, 0, 0);

        var rows = new List<CyclicTriangleJoinRow>(Math.Min(options.MaxResults, 1024));
        long work = 0;
        int peak = 0;
        foreach (CyclicTriangleJoinPair pair in root)
        {
            if (!thirdBySecond.TryGetValue(pair.Right, out VertexId[]? left)
                || !thirdByFirst.TryGetValue(pair.Left, out VertexId[]? right))
                continue;

            peak = Math.Max(peak, left.Length + right.Length);
            int i = 0;
            int j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (!TryCharge(options, elapsed, ref work, out stop))
                    return Result(rows.ToArray(), CyclicTriangleJoinStrategy.TransientColumns,
                        CyclicTriangleJoinRouteReason.DenseCyclic, stop, estimatedIntermediateRows, work, peak);

                int comparison = left[i].Value.CompareTo(right[j].Value);
                if (comparison < 0) { i++; continue; }
                if (comparison > 0) { j++; continue; }
                if (rows.Count == options.MaxResults)
                {
                    return Result(rows.ToArray(), CyclicTriangleJoinStrategy.TransientColumns,
                        CyclicTriangleJoinRouteReason.DenseCyclic,
                        CyclicTriangleJoinTerminationReason.MaxResultsReached,
                        estimatedIntermediateRows, work, peak);
                }

                rows.Add(new(pair.Left, pair.Right, left[i]));
                i++;
                j++;
            }
        }

        return Result(rows.ToArray(), CyclicTriangleJoinStrategy.TransientColumns,
            CyclicTriangleJoinRouteReason.DenseCyclic, CyclicTriangleJoinTerminationReason.Completed,
            estimatedIntermediateRows, work, peak);
    }

    private static CyclicTriangleJoinResult ExecuteMaterializing(
        IReadOnlyList<CyclicTriangleJoinPair> firstToSecond,
        IReadOnlyList<CyclicTriangleJoinPair> secondToThird,
        IReadOnlyList<CyclicTriangleJoinPair> thirdToFirst,
        long estimatedIntermediateRows,
        CyclicTriangleJoinRouteReason routeReason,
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed)
    {
        if (estimatedIntermediateRows > options.MaxMaterializedIntermediateRows || estimatedIntermediateRows > int.MaxValue)
        {
            return Result([], CyclicTriangleJoinStrategy.Materializing, routeReason,
                CyclicTriangleJoinTerminationReason.IntermediateLimitReached,
                estimatedIntermediateRows, 0, 0);
        }

        if (!ObserveRuntime(options, elapsed, out var stop))
            return Result([], CyclicTriangleJoinStrategy.Materializing, routeReason, stop, estimatedIntermediateRows, 0, 0);

        CyclicTriangleJoinPair[] root = firstToSecond.ToArray();
        Array.Sort(root, ComparePair);
        Dictionary<VertexId, VertexId[]> thirdBySecond = BuildColumns(secondToThird, reverse: false, options, elapsed, out stop);
        if (stop != CyclicTriangleJoinTerminationReason.Completed)
            return Result([], CyclicTriangleJoinStrategy.Materializing, routeReason, stop, estimatedIntermediateRows, 0, 0);

        var intermediate = new List<CyclicTriangleJoinRow>((int)estimatedIntermediateRows);
        long work = 0;
        foreach (CyclicTriangleJoinPair pair in root)
        {
            if (!thirdBySecond.TryGetValue(pair.Right, out VertexId[]? thirds)) continue;
            foreach (VertexId third in thirds)
            {
                if (!TryCharge(options, elapsed, ref work, out stop))
                    return Result([], CyclicTriangleJoinStrategy.Materializing, routeReason, stop,
                        estimatedIntermediateRows, work, intermediate.Count);
                intermediate.Add(new(pair.Left, pair.Right, third));
            }
        }

        intermediate.Sort(CompareRow);
        Dictionary<(VertexId Third, VertexId First), int> closingMultiplicity = BuildMultiplicity(
            thirdToFirst, options, elapsed, out stop);
        if (stop != CyclicTriangleJoinTerminationReason.Completed)
            return Result([], CyclicTriangleJoinStrategy.Materializing, routeReason, stop,
                estimatedIntermediateRows, work, intermediate.Count);

        var rows = new List<CyclicTriangleJoinRow>(Math.Min(options.MaxResults, 1024));
        foreach (CyclicTriangleJoinRow row in intermediate)
        {
            if (!TryCharge(options, elapsed, ref work, out stop))
                return Result(rows.ToArray(), CyclicTriangleJoinStrategy.Materializing, routeReason, stop,
                    estimatedIntermediateRows, work, intermediate.Count);
            int multiplicity = closingMultiplicity.GetValueOrDefault((row.Third, row.First));
            for (int i = 0; i < multiplicity; i++)
            {
                if ((i & 63) == 0 && !ObserveRuntime(options, elapsed, out stop))
                    return Result(rows.ToArray(), CyclicTriangleJoinStrategy.Materializing, routeReason, stop,
                        estimatedIntermediateRows, work, intermediate.Count);
                if (rows.Count == options.MaxResults)
                {
                    return Result(rows.ToArray(), CyclicTriangleJoinStrategy.Materializing, routeReason,
                        CyclicTriangleJoinTerminationReason.MaxResultsReached,
                        estimatedIntermediateRows, work, intermediate.Count);
                }
                rows.Add(row);
            }
        }

        return Result(rows.ToArray(), CyclicTriangleJoinStrategy.Materializing, routeReason,
            CyclicTriangleJoinTerminationReason.Completed, estimatedIntermediateRows, work, intermediate.Count);
    }

    private static Dictionary<VertexId, VertexId[]> BuildColumns(
        IReadOnlyList<CyclicTriangleJoinPair> relation,
        bool reverse,
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed,
        out CyclicTriangleJoinTerminationReason stop)
    {
        var lists = new Dictionary<VertexId, List<VertexId>>();
        for (int i = 0; i < relation.Count; i++)
        {
            if ((i & 255) == 0 && !ObserveRuntime(options, elapsed, out stop)) return [];
            CyclicTriangleJoinPair pair = relation[i];
            VertexId key = reverse ? pair.Right : pair.Left;
            VertexId value = reverse ? pair.Left : pair.Right;
            if (!lists.TryGetValue(key, out List<VertexId>? values)) lists.Add(key, values = []);
            values.Add(value);
        }

        var columns = new Dictionary<VertexId, VertexId[]>(lists.Count);
        foreach ((VertexId key, List<VertexId> values) in lists)
        {
            values.Sort(static (left, right) => left.Value.CompareTo(right.Value));
            columns.Add(key, values.ToArray());
        }
        stop = CyclicTriangleJoinTerminationReason.Completed;
        return columns;
    }

    private static Dictionary<(VertexId Third, VertexId First), int> BuildMultiplicity(
        IReadOnlyList<CyclicTriangleJoinPair> relation,
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed,
        out CyclicTriangleJoinTerminationReason stop)
    {
        var result = new Dictionary<(VertexId, VertexId), int>();
        for (int i = 0; i < relation.Count; i++)
        {
            if ((i & 255) == 0 && !ObserveRuntime(options, elapsed, out stop)) return [];
            CyclicTriangleJoinPair pair = relation[i];
            var key = (pair.Left, pair.Right);
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        stop = CyclicTriangleJoinTerminationReason.Completed;
        return result;
    }

    private static bool ContainsDuplicate(
        IReadOnlyList<CyclicTriangleJoinPair> relation,
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed,
        out CyclicTriangleJoinTerminationReason stop)
    {
        var seen = new HashSet<CyclicTriangleJoinPair>();
        for (int i = 0; i < relation.Count; i++)
        {
            if ((i & 255) == 0 && !ObserveRuntime(options, elapsed, out stop)) return false;
            if (!seen.Add(relation[i]))
            {
                stop = CyclicTriangleJoinTerminationReason.Completed;
                return true;
            }
        }
        stop = CyclicTriangleJoinTerminationReason.Completed;
        return false;
    }

    private static long EstimateIntermediate(
        IReadOnlyList<CyclicTriangleJoinPair> firstToSecond,
        IReadOnlyList<CyclicTriangleJoinPair> secondToThird,
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed,
        out CyclicTriangleJoinTerminationReason stop)
    {
        var degree = new Dictionary<VertexId, int>();
        for (int i = 0; i < secondToThird.Count; i++)
        {
            if ((i & 255) == 0 && !ObserveRuntime(options, elapsed, out stop)) return 0;
            VertexId second = secondToThird[i].Left;
            degree[second] = degree.GetValueOrDefault(second) + 1;
        }

        long estimate = 0;
        for (int i = 0; i < firstToSecond.Count; i++)
        {
            if ((i & 255) == 0 && !ObserveRuntime(options, elapsed, out stop)) return estimate;
            estimate = SaturatingAdd(estimate, degree.GetValueOrDefault(firstToSecond[i].Right));
        }
        stop = CyclicTriangleJoinTerminationReason.Completed;
        return estimate;
    }

    private static bool TryCharge(
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed,
        ref long work,
        out CyclicTriangleJoinTerminationReason stop)
    {
        if (work >= options.MaxWork)
        {
            stop = CyclicTriangleJoinTerminationReason.WorkBudgetReached;
            return false;
        }
        work++;
        if ((work & 63) == 0) return ObserveRuntime(options, elapsed, out stop);
        stop = CyclicTriangleJoinTerminationReason.Completed;
        return true;
    }

    private static bool ObserveRuntime(
        CyclicTriangleJoinOptions options,
        Stopwatch elapsed,
        out CyclicTriangleJoinTerminationReason stop)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (options.TimeLimit != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= options.TimeLimit)
        {
            stop = CyclicTriangleJoinTerminationReason.TimeBudgetReached;
            return false;
        }
        stop = CyclicTriangleJoinTerminationReason.Completed;
        return true;
    }

    private static void ValidateOptions(CyclicTriangleJoinOptions options)
    {
        if (options.MaxResults <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxResults must be positive.");
        if (options.MaxInputRows <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxInputRows must be positive.");
        if (options.MaxWork <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxWork must be positive.");
        if (options.MaxMaterializedIntermediateRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMaterializedIntermediateRows must be positive.");
        if (options.TimeLimit < TimeSpan.Zero && options.TimeLimit != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "TimeLimit must be non-negative or infinite.");
    }

    private static int ComparePair(CyclicTriangleJoinPair left, CyclicTriangleJoinPair right)
    {
        int first = left.Left.Value.CompareTo(right.Left.Value);
        return first != 0 ? first : left.Right.Value.CompareTo(right.Right.Value);
    }

    private static int CompareRow(CyclicTriangleJoinRow left, CyclicTriangleJoinRow right)
    {
        int first = left.First.Value.CompareTo(right.First.Value);
        if (first != 0) return first;
        int second = left.Second.Value.CompareTo(right.Second.Value);
        return second != 0 ? second : left.Third.Value.CompareTo(right.Third.Value);
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long left, long right)
        => left == 0 || right == 0 ? 0 : left > long.MaxValue / right ? long.MaxValue : left * right;

    private static long TotalInputRows(
        IReadOnlyList<CyclicTriangleJoinPair> firstToSecond,
        IReadOnlyList<CyclicTriangleJoinPair> secondToThird,
        IReadOnlyList<CyclicTriangleJoinPair> thirdToFirst)
        => SaturatingAdd(firstToSecond.Count, SaturatingAdd(secondToThird.Count, thirdToFirst.Count));

    private static CyclicTriangleJoinResult Result(
        CyclicTriangleJoinRow[] rows,
        CyclicTriangleJoinStrategy strategy,
        CyclicTriangleJoinRouteReason routeReason,
        CyclicTriangleJoinTerminationReason terminationReason,
        long estimatedIntermediateRows,
        long work,
        int peakWorkingRows)
        => new(rows, strategy, routeReason, terminationReason, estimatedIntermediateRows, work, peakWorkingRows);

    private readonly record struct Analysis(
        long EstimatedIntermediateRows,
        CyclicTriangleJoinRouteReason RouteReason,
        CyclicTriangleJoinTerminationReason TerminationReason);
}
