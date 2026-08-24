using System.Diagnostics;
using Yatagarasu.Core;

namespace Yatagarasu;

/// <summary>最小ヒッティング集合の探索が終了した理由。</summary>
internal enum MinimumHittingSetTerminationReason
{
    /// <summary>上下界が一致し、最小性を証明した。</summary>
    Optimal,
    /// <summary>空の制約集合を含むため、実行可能解が存在しないことを証明した。</summary>
    Infeasible,
    /// <summary>探索ノード数の上限に達した。</summary>
    NodeBudget,
    /// <summary>探索時間の上限に達した。</summary>
    TimeBudget,
    /// <summary>キャンセルが要求された。</summary>
    Cancelled,
}

/// <summary>
/// 最小ヒッティング集合探索の協調的な実行上限。
/// 初回の実行可能証明書を作るための入力正規化とfallback構築は中断しない。
/// </summary>
internal sealed class MinimumHittingSetOptions
{
    /// <summary>訪問する分枝限定ノードの最大数。0では正規化後のfallback解と証明済み下界を返す。</summary>
    public long MaxNodes { get; init; } = 1_000_000;

    /// <summary>
    /// solver時間の協調的な上限。既定値は1秒。無制限にする場合は <see cref="Timeout.InfiniteTimeSpan"/>。
    /// 初回の入力正規化とfallback構築は完了後に観測し、transaction adapterのsnapshot materializationは計時前に行う。
    /// </summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 探索の協調的なキャンセル。初回の入力正規化とfallback構築の完了後に観測し、
    /// キャンセル時も実行可能解と証明済み上下界を返す。
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>最小ヒッティング集合探索の結果。</summary>
internal sealed class MinimumHittingSetResult
{
    internal MinimumHittingSetResult(
        bool hasSolution,
        IReadOnlyList<VertexId> solution,
        int? upperBound,
        int? lowerBound,
        bool isOptimal,
        MinimumHittingSetTerminationReason terminationReason,
        long visitedNodes,
        int factCount,
        int candidateCount,
        int removedFactCount,
        int removedCandidateCount)
    {
        HasSolution = hasSolution;
        Solution = solution;
        UpperBound = upperBound;
        LowerBound = lowerBound;
        IsOptimal = isOptimal;
        TerminationReason = terminationReason;
        VisitedNodes = visitedNodes;
        FactCount = factCount;
        CandidateCount = candidateCount;
        RemovedFactCount = removedFactCount;
        RemovedCandidateCount = removedCandidateCount;
    }

    /// <summary>全制約を被覆する解が存在するか。</summary>
    public bool HasSolution { get; }

    /// <summary>全制約を被覆する実行可能解。<see cref="HasSolution"/> が偽なら空。</summary>
    public IReadOnlyList<VertexId> Solution { get; }

    /// <summary>最適解の要素数に対する上界。実行不可能なら <c>null</c>。</summary>
    public int? UpperBound { get; }

    /// <summary>最適解の要素数に対する証明済み下界。実行不可能なら <c>null</c>。</summary>
    public int? LowerBound { get; }

    /// <summary>最小性または実行不可能性を証明したか。</summary>
    public bool IsOptimal { get; }

    /// <summary>探索が終了した理由。</summary>
    public MinimumHittingSetTerminationReason TerminationReason { get; }

    /// <summary>訪問した分枝限定ノード数。</summary>
    public long VisitedNodes { get; }

    /// <summary>入力された制約集合の数。</summary>
    public int FactCount { get; }

    /// <summary>入力に現れた相異なる候補Vertexの数。</summary>
    public int CandidateCount { get; }

    /// <summary>包含関係により探索前に除去した冗長な制約集合の数。</summary>
    public int RemovedFactCount { get; }

    /// <summary>被覆関係により探索前に除去した冗長な候補Vertexの数。</summary>
    public int RemovedCandidateCount { get; }
}

/// <summary>Vertex集合族に対する最小ヒッティング集合を求めるアルゴリズム。</summary>
internal static class MinimumHittingSetAlgorithms
{
    /// <summary>
    /// 各内側集合を少なくとも一つのVertexで被覆する最小集合を探索する。
    /// 常に実行可能な暫定解と証明済み上下界を返し、上限内に一致すれば最小性を証明する。
    /// 空の入力には空の最適解を返し、空の内側集合を含む入力は実行不可能と判定する。
    /// 実行可能証明書に全制約が必要なため、最初に入力全体を正規化してfallback解を構築してから
    /// node、time、cancellationの協調的な上限を観測する。
    /// </summary>
    public static MinimumHittingSetResult Solve(
        IReadOnlyList<IReadOnlyList<VertexId>> facts,
        MinimumHittingSetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        options ??= new MinimumHittingSetOptions();
        ValidateOptions(options);
        return Solver.Solve(facts, options);
    }

    /// <summary>
    /// 指定型の各Nexusを一つの制約集合とし、指定ロールのmember Vertexから最小ヒッティング集合を求める。
    /// Nexusとmemberは読み取りトランザクションの開始時snapshotから取得するため、未commitの変更と
    /// 読み取り開始後にcommitされた変更は含まない。
    /// snapshot入力を全てmaterializeした後にsolverを開始するため、solverのtime budgetとcancellationは
    /// adapter走査を中断しない。
    /// </summary>
    public static MinimumHittingSetResult FindMinimumHittingSet(
        this IReadTransaction transaction,
        string nexusType,
        string coverRole,
        MinimumHittingSetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(nexusType);
        ArgumentException.ThrowIfNullOrWhiteSpace(coverRole);
        options ??= new MinimumHittingSetOptions();
        ValidateOptions(options);

        var facts = new List<IReadOnlyList<VertexId>>();
        foreach (NexusId nexus in transaction.Query.Nexuses().ToList())
        {
            if (!string.Equals(transaction.GetNexusType(nexus), nexusType, StringComparison.Ordinal)) continue;
            var candidates = new List<VertexId>();
            var members = transaction.GetMembers(nexus, coverRole);
            try
            {
                while (members.MoveNext()) candidates.Add(members.Current.VertexId);
            }
            finally
            {
                members.Dispose();
            }
            facts.Add(candidates);
        }
        return Solver.Solve(facts, options);
    }

    private static void ValidateOptions(MinimumHittingSetOptions options)
    {
        if (options.MaxNodes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxNodesは0以上でなければなりません。");
        if (options.TimeLimit < TimeSpan.Zero && options.TimeLimit != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "TimeLimitは0以上または無制限でなければなりません。");
    }

    private static class Solver
    {
        internal static MinimumHittingSetResult Solve(
            IReadOnlyList<IReadOnlyList<VertexId>> inputFacts,
            MinimumHittingSetOptions options)
        {
            var elapsed = Stopwatch.StartNew();
            MinimumHittingSetTerminationReason? preparationStop = null;
            bool PreparationShouldStop()
            {
                if (preparationStop is not null) return true;
                if (options.CancellationToken.IsCancellationRequested)
                    preparationStop = MinimumHittingSetTerminationReason.Cancelled;
                else if (options.TimeLimit != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= options.TimeLimit)
                    preparationStop = MinimumHittingSetTerminationReason.TimeBudget;
                return preparationStop is not null;
            }

            Preparation preparation = Prepare(inputFacts, PreparationShouldStop);
            if (preparation.Infeasible)
            {
                return new(false, [], null, null, true, MinimumHittingSetTerminationReason.Infeasible, 0,
                    preparation.InputFactCount, preparation.InputCandidateCount, 0, 0);
            }
            if (preparation.InputFactCount == 0)
            {
                return new(true, [], 0, 0, true, MinimumHittingSetTerminationReason.Optimal, 0,
                    0, 0, 0, 0);
            }
            if (preparation.Problem is null)
            {
                return new(true, preparation.FallbackSolution, preparation.FallbackSolution.Length, 1, false,
                    preparationStop!.Value, 0, preparation.InputFactCount, preparation.InputCandidateCount, 0, 0);
            }
            PreparedProblem problem = preparation.Problem;

            MinimumHittingSetTerminationReason? postPreparationStop = StopReason(options, elapsed, 0);
            if (postPreparationStop is MinimumHittingSetTerminationReason postPreparationReason)
            {
                return Result(true, preparation.FallbackSolution, preparation.FallbackSolution.Length, 1, false,
                    postPreparationReason, 0, problem);
            }

            int[] incumbent = Greedy(problem);
            MinimumHittingSetTerminationReason? initialStop = StopReason(options, elapsed, 0);
            if (initialStop is MinimumHittingSetTerminationReason initialReason)
                return Result(true, MapSolution(problem, incumbent), incumbent.Length, 0, false, initialReason, 0, problem);

            MinimumHittingSetTerminationReason? rootStop = null;
            bool RootShouldStop()
            {
                rootStop ??= StopReason(options, elapsed, 0);
                return rootStop is not null;
            }
            if (!TryCalculateBounds(problem, new bool[problem.Facts.Length], RootShouldStop, out BoundReport rootBounds))
            {
                return Result(true, MapSolution(problem, incumbent), incumbent.Length, 0, false,
                    rootStop!.Value, 0, problem);
            }
            int lowerBound = Math.Max(rootBounds.SimpleCoverage, rootBounds.Packing);
            int upperBound = incumbent.Length;
            if (lowerBound >= upperBound)
                return Result(true, MapSolution(problem, incumbent), upperBound, upperBound, true,
                    MinimumHittingSetTerminationReason.Optimal, 0, problem);

            var search = new DecisionSearch(problem, options, elapsed);
            for (int cardinality = lowerBound; cardinality < upperBound; cardinality++)
            {
                DecisionResult decision = search.Decide(cardinality);
                if (decision.StopReason is MinimumHittingSetTerminationReason stopReason)
                {
                    return Result(true, MapSolution(problem, incumbent), upperBound, lowerBound, false,
                        stopReason, search.VisitedNodes, problem);
                }
                if (decision.Solution is not null)
                {
                    incumbent = decision.Solution;
                    upperBound = incumbent.Length;
                    return Result(true, MapSolution(problem, incumbent), upperBound, upperBound, true,
                        MinimumHittingSetTerminationReason.Optimal, search.VisitedNodes, problem);
                }
                lowerBound = cardinality + 1;
            }

            return Result(true, MapSolution(problem, incumbent), upperBound, upperBound, true,
                MinimumHittingSetTerminationReason.Optimal, search.VisitedNodes, problem);
        }

        private static MinimumHittingSetResult Result(
            bool hasSolution,
            VertexId[] solution,
            int? upperBound,
            int? lowerBound,
            bool isOptimal,
            MinimumHittingSetTerminationReason reason,
            long visitedNodes,
            PreparedProblem problem)
            => new(hasSolution, solution, upperBound, lowerBound, isOptimal, reason, visitedNodes,
                problem.InputFactCount, problem.Candidates.Length, problem.RemovedFacts, problem.RemovedCandidates);

        private static Preparation Prepare(
            IReadOnlyList<IReadOnlyList<VertexId>> inputFacts,
            Func<bool> shouldStop)
        {
            var candidateSet = new SortedSet<VertexId>(VertexIdComparer.Instance);
            var normalized = new VertexId[inputFacts.Count][];
            for (int fact = 0; fact < inputFacts.Count; fact++)
            {
                IReadOnlyList<VertexId> values = inputFacts[fact]
                    ?? throw new ArgumentException("制約集合にnullを含めることはできません。", nameof(inputFacts));
                var distinct = new SortedSet<VertexId>(VertexIdComparer.Instance);
                for (int candidate = 0; candidate < values.Count; candidate++)
                {
                    VertexId id = values[candidate];
                    if (!id.IsValid)
                        throw new ArgumentException("候補には有効なVertexIdが必要です。", nameof(inputFacts));
                    distinct.Add(id);
                    candidateSet.Add(id);
                }
                normalized[fact] = distinct.ToArray();
            }

            VertexId[] candidates = candidateSet.ToArray();
            if (normalized.Any(static fact => fact.Length == 0))
                return new(null, [], inputFacts.Count, candidates.Length, true);
            if (inputFacts.Count == 0)
                return new(null, [], 0, 0, false);

            VertexId[] fallbackSolution = normalized
                .Select(static fact => fact[0])
                .Distinct()
                .OrderBy(static id => id.Value)
                .ToArray();
            if (shouldStop())
                return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);

            var candidateIndex = new Dictionary<VertexId, int>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++) candidateIndex.Add(candidates[i], i);
            int[][] indexed = normalized
                .Select(fact => fact.Select(id => candidateIndex[id]).ToArray())
                .ToArray();

            var orderedFacts = indexed.OrderBy(static fact => fact.Length).ThenBy(static fact => fact, IntArrayComparer.Instance);
            var kept = new List<int[]>();
            int removedFacts = 0;
            foreach (int[] fact in orderedFacts)
            {
                if (shouldStop())
                    return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);
                bool redundant = false;
                foreach (int[] other in kept)
                {
                    if (shouldStop())
                        return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);
                    if (!IsSubset(other, fact)) continue;
                    redundant = true;
                    break;
                }
                if (redundant)
                {
                    removedFacts++;
                    continue;
                }
                kept.Add(fact);
            }

            int[][] facts = kept.ToArray();
            var coverage = new bool[candidates.Length][];
            for (int candidate = 0; candidate < candidates.Length; candidate++)
            {
                if (shouldStop())
                    return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);
                coverage[candidate] = new bool[facts.Length];
                for (int fact = 0; fact < facts.Length; fact++)
                {
                    if (shouldStop())
                        return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);
                    coverage[candidate][fact] = Array.BinarySearch(facts[fact], candidate) >= 0;
                }
            }

            bool[] active = Enumerable.Repeat(true, candidates.Length).ToArray();
            int removedCandidates = 0;
            for (int candidate = 0; candidate < candidates.Length; candidate++)
            {
                if (shouldStop())
                    return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);
                if (!coverage[candidate].Any(static covered => covered))
                {
                    active[candidate] = false;
                    removedCandidates++;
                    continue;
                }
                for (int other = 0; other < candidates.Length; other++)
                {
                    if (shouldStop())
                        return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);
                    if (candidate == other || !IsSubset(coverage[candidate], coverage[other])) continue;
                    bool equal = IsSubset(coverage[other], coverage[candidate]);
                    if (equal && candidate < other) continue;
                    active[candidate] = false;
                    removedCandidates++;
                    break;
                }
            }

            for (int candidate = 0; candidate < candidates.Length; candidate++)
            {
                if (shouldStop())
                    return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);
                if (!active[candidate]) Array.Clear(coverage[candidate]);
            }
            var options = new int[facts.Length][];
            for (int fact = 0; fact < facts.Length; fact++)
            {
                if (shouldStop())
                    return new(null, fallbackSolution, inputFacts.Count, candidates.Length, false);
                options[fact] = facts[fact].Where(candidate => active[candidate]).ToArray();
            }
            bool infeasible = options.Any(static fact => fact.Length == 0);
            return new(
                new(options, candidates, coverage, infeasible, inputFacts.Count, removedCandidates, removedFacts),
                fallbackSolution,
                inputFacts.Count,
                candidates.Length,
                infeasible);
        }

        private static int[] Greedy(PreparedProblem problem)
        {
            var covered = new bool[problem.Facts.Length];
            var selected = new List<int>();
            while (covered.Any(static value => !value))
            {
                int bestCandidate = -1;
                int bestCoverage = 0;
                for (int candidate = 0; candidate < problem.Candidates.Length; candidate++)
                {
                    int added = CountNewCoverage(problem.Coverage[candidate], covered);
                    if (added <= bestCoverage) continue;
                    bestCoverage = added;
                    bestCandidate = candidate;
                }
                if (bestCandidate < 0) throw new InvalidOperationException("実行可能な初期解を構築できませんでした。");
                selected.Add(bestCandidate);
                AddCoverage(covered, problem.Coverage[bestCandidate]);
            }
            selected.Sort();
            return selected.ToArray();
        }

        private static bool TryCalculateBounds(
            PreparedProblem problem,
            bool[] covered,
            Func<bool> shouldStop,
            out BoundReport report)
        {
            int uncovered = covered.Count(static value => !value);
            if (uncovered == 0)
            {
                report = new(0, 0);
                return true;
            }
            int maximumCoverage = 0;
            for (int candidate = 0; candidate < problem.Candidates.Length; candidate++)
            {
                if (shouldStop())
                {
                    report = default;
                    return false;
                }
                maximumCoverage = Math.Max(maximumCoverage, CountNewCoverage(problem.Coverage[candidate], covered));
            }
            int simple = maximumCoverage == 0 ? int.MaxValue : (uncovered + maximumCoverage - 1) / maximumCoverage;

            var used = new bool[problem.Candidates.Length];
            int packing = 0;
            int[] orderedFacts = Enumerable.Range(0, problem.Facts.Length)
                .Where(fact => !covered[fact])
                .OrderBy(fact => problem.Facts[fact].Length)
                .ThenBy(static fact => fact)
                .ToArray();
            foreach (int fact in orderedFacts)
            {
                if (shouldStop())
                {
                    report = default;
                    return false;
                }
                if (problem.Facts[fact].Any(candidate => used[candidate])) continue;
                foreach (int candidate in problem.Facts[fact]) used[candidate] = true;
                packing++;
            }
            report = new(simple, packing);
            return true;
        }

        private static VertexId[] MapSolution(PreparedProblem problem, int[] solution)
            => solution.Select(candidate => problem.Candidates[candidate]).OrderBy(static id => id.Value).ToArray();

        private static MinimumHittingSetTerminationReason? StopReason(
            MinimumHittingSetOptions options,
            Stopwatch elapsed,
            long visitedNodes)
        {
            if (options.CancellationToken.IsCancellationRequested)
                return MinimumHittingSetTerminationReason.Cancelled;
            if (options.TimeLimit != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= options.TimeLimit)
                return MinimumHittingSetTerminationReason.TimeBudget;
            if (visitedNodes >= options.MaxNodes)
                return MinimumHittingSetTerminationReason.NodeBudget;
            return null;
        }

        private sealed class DecisionSearch(
            PreparedProblem problem,
            MinimumHittingSetOptions options,
            Stopwatch elapsed)
        {
            private MinimumHittingSetTerminationReason? _stopReason;

            internal long VisitedNodes { get; private set; }

            internal DecisionResult Decide(int cardinality)
            {
                int[]? solution = Search(
                    cardinality,
                    new bool[problem.Facts.Length],
                    [],
                    new bool[problem.Candidates.Length]);
                return new(solution, _stopReason);
            }

            private int[]? Search(int limit, bool[] covered, List<int> selected, bool[] selectedFlags)
            {
                if (ShouldStop()) return null;
                VisitedNodes++;
                if (covered.All(static value => value)) return selected.Order().ToArray();
                if (selected.Count >= limit) return null;

                if (!TryCalculateBounds(problem, covered, ShouldStop, out BoundReport bounds)) return null;
                if (selected.Count + Math.Max(bounds.SimpleCoverage, bounds.Packing) > limit) return null;
                int branchFact = -1;
                int branchWidth = int.MaxValue;
                for (int fact = 0; fact < problem.Facts.Length; fact++)
                {
                    if (ShouldStop()) return null;
                    if (covered[fact]) continue;
                    int width = problem.Facts[fact].Count(candidate => !selectedFlags[candidate]);
                    if (width >= branchWidth) continue;
                    branchWidth = width;
                    branchFact = fact;
                }
                var scoredOptions = new List<(int Candidate, int Added)>(problem.Facts[branchFact].Length);
                foreach (int candidate in problem.Facts[branchFact])
                {
                    if (ShouldStop()) return null;
                    if (!selectedFlags[candidate])
                        scoredOptions.Add((candidate, CountNewCoverage(problem.Coverage[candidate], covered)));
                }
                int[] branchOptions = scoredOptions
                    .OrderByDescending(static option => option.Added)
                    .ThenBy(static option => option.Candidate)
                    .Select(static option => option.Candidate)
                    .ToArray();
                if (ShouldStop()) return null;
                foreach (int candidate in branchOptions)
                {
                    var nextCovered = (bool[])covered.Clone();
                    AddCoverage(nextCovered, problem.Coverage[candidate]);
                    selected.Add(candidate);
                    selectedFlags[candidate] = true;
                    int[]? solution = Search(limit, nextCovered, selected, selectedFlags);
                    selectedFlags[candidate] = false;
                    selected.RemoveAt(selected.Count - 1);
                    if (solution is not null || _stopReason is not null) return solution;
                }
                return null;
            }

            private bool ShouldStop()
            {
                _stopReason ??= StopReason(options, elapsed, VisitedNodes);
                return _stopReason is not null;
            }
        }

        private static int CountNewCoverage(bool[] candidateCoverage, bool[] covered)
        {
            int count = 0;
            for (int fact = 0; fact < covered.Length; fact++)
                if (!covered[fact] && candidateCoverage[fact]) count++;
            return count;
        }

        private static void AddCoverage(bool[] covered, bool[] candidateCoverage)
        {
            for (int fact = 0; fact < covered.Length; fact++) covered[fact] |= candidateCoverage[fact];
        }

        private static bool IsSubset(int[] subset, int[] superset)
        {
            int left = 0;
            int right = 0;
            while (left < subset.Length && right < superset.Length)
            {
                if (subset[left] == superset[right]) { left++; right++; }
                else if (subset[left] > superset[right]) right++;
                else return false;
            }
            return left == subset.Length;
        }

        private static bool IsSubset(bool[] subset, bool[] superset)
        {
            for (int i = 0; i < subset.Length; i++)
                if (subset[i] && !superset[i]) return false;
            return true;
        }

        private sealed record PreparedProblem(
            int[][] Facts,
            VertexId[] Candidates,
            bool[][] Coverage,
            bool Infeasible,
            int InputFactCount,
            int RemovedCandidates,
            int RemovedFacts);

        private sealed record Preparation(
            PreparedProblem? Problem,
            VertexId[] FallbackSolution,
            int InputFactCount,
            int InputCandidateCount,
            bool Infeasible);

        private readonly record struct BoundReport(int SimpleCoverage, int Packing);
        private readonly record struct DecisionResult(
            int[]? Solution,
            MinimumHittingSetTerminationReason? StopReason);

        private sealed class VertexIdComparer : IComparer<VertexId>
        {
            internal static readonly VertexIdComparer Instance = new();
            public int Compare(VertexId x, VertexId y) => x.Value.CompareTo(y.Value);
        }

        private sealed class IntArrayComparer : IComparer<int[]>
        {
            internal static readonly IntArrayComparer Instance = new();
            public int Compare(int[]? x, int[]? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is null) return -1;
                if (y is null) return 1;
                int length = Math.Min(x.Length, y.Length);
                for (int i = 0; i < length; i++)
                {
                    int comparison = x[i].CompareTo(y[i]);
                    if (comparison != 0) return comparison;
                }
                return x.Length.CompareTo(y.Length);
            }
        }
    }
}
