using System.Diagnostics;
using System.Runtime.InteropServices;
using Yatagarasu.Core;

namespace Yatagarasu.Benchmarks;

internal static class MinimumHittingSetBenchmarks
{
    private const long NodeBudget = 20_000;
    private static readonly TimeSpan TimeBudget = TimeSpan.FromMilliseconds(100);
    private const double CaseLimitMilliseconds = 5_000;
    private const long CaseAllocationLimit = 128L * 1024 * 1024;
    private static readonly TimeSpan TotalLimit = TimeSpan.FromSeconds(45);

    internal static int Run()
    {
        var total = Stopwatch.StartNew();
        Console.WriteLine("=== Minimum hitting set (product API) ===");
        Console.WriteLine(
            $"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} " +
            $"arch={RuntimeInformation.ProcessArchitecture} processorCount={Environment.ProcessorCount} " +
            "maxNodes=20000 timeLimitMs=100 caseLimitMs=5000 allocationLimitBytes=134217728 totalLimitMs=45000");
        try
        {
            StageReport small = RunStage(100, 40);
            int maxFacts = small.FactCount;
            int maxCandidates = small.CandidateCount;
            if (CanRunNext(small, 250, 100, total, out Projection mediumProjection))
            {
                PrintProjection(mediumProjection, "RUN");
                StageReport medium = RunStage(250, 100);
                maxFacts = medium.FactCount;
                maxCandidates = medium.CandidateCount;
                if (CanRunNext(medium, 500, 200, total, out Projection largeProjection))
                {
                    PrintProjection(largeProjection, "RUN");
                    StageReport large = RunStage(500, 200);
                    maxFacts = large.FactCount;
                    maxCandidates = large.CandidateCount;
                }
                else
                {
                    PrintProjection(largeProjection, "SKIP");
                }
            }
            else
            {
                PrintProjection(mediumProjection, "SKIP");
                Console.WriteLine("minimum-hitting-set-stage facts=500 candidates=200 status=SKIP reason=preceding-stage-not-run");
            }
            Console.WriteLine(
                $"minimum-hitting-set-summary maxMeasuredFacts={maxFacts} maxMeasuredCandidates={maxCandidates} " +
                $"totalMs={total.Elapsed.TotalMilliseconds:F3} status=PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"minimum-hitting-set-summary status=FAIL type={ex.GetType().Name} message={ex.Message}");
            return 1;
        }
    }

    private static StageReport RunStage(int factCount, int candidateCount)
    {
        var shapes = new (string Name, int[][] Facts)[]
        {
            ("dominated", CreateDominated(factCount, candidateCount)),
            ("symmetric", CreateSymmetric(candidateCount / 5, 5, factCount - candidateCount / 5 * 10)),
            ("packing-anchor", CreatePackingAnchor(factCount, candidateCount)),
            ("uniform-hard", CreateUniformHard(factCount, candidateCount, 0x554E0000 + factCount)),
        };
        var reports = new List<CaseReport>();
        foreach ((string shape, int[][] facts) in shapes)
        {
            using CaseData data = CreateDatabase(facts, candidateCount);
            CaseReport report = MeasureCase(data.Read);
            reports.Add(report);
            MinimumHittingSetResult result = report.Result;
            Console.WriteLine(
                $"minimum-hitting-set-case shape={shape} facts={factCount} candidates={candidateCount} " +
                $"medianMs={report.MedianMilliseconds:F3} spreadPct={report.SpreadPercent:F2} " +
                $"allocBytes={report.AllocatedBytes} visitedNodes={result.VisitedNodes} " +
                $"removedCandidates={result.RemovedCandidateCount} removedFacts={result.RemovedFactCount} " +
                $"lower={result.LowerBound?.ToString() ?? "none"} upper={result.UpperBound?.ToString() ?? "none"} " +
                $"isOptimal={result.IsOptimal.ToString().ToLowerInvariant()} reason={result.TerminationReason}");
        }
        return new(factCount, candidateCount, reports);
    }

    private static CaseReport MeasureCase(IReadTransaction read)
    {
        _ = Solve(read);
        var elapsed = new double[3];
        MinimumHittingSetResult result = null!;
        for (int sample = 0; sample < elapsed.Length; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            result = Solve(read);
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Validate(result);
        }
        Array.Sort(elapsed);
        _ = Solve(read);
        long before = GC.GetAllocatedBytesForCurrentThread();
        MinimumHittingSetResult allocationResult = Solve(read);
        long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Validate(allocationResult);
        double median = elapsed[elapsed.Length / 2];
        double spread = median == 0 ? 0 : (elapsed[^1] - elapsed[0]) / median * 100;
        if (median > CaseLimitMilliseconds || allocation > CaseAllocationLimit)
            throw new InvalidOperationException("case resource limit exceeded");
        return new(median, spread, allocation, result);
    }

    private static MinimumHittingSetResult Solve(IReadTransaction read) => read.FindMinimumHittingSet(
        "Constraint",
        "candidate",
        new MinimumHittingSetOptions { MaxNodes = NodeBudget, TimeLimit = TimeBudget });

    private static void Validate(MinimumHittingSetResult result)
    {
        if (!result.HasSolution || result.LowerBound is null || result.UpperBound is null
            || result.LowerBound > result.UpperBound || result.UpperBound != result.Solution.Count
            || result.VisitedNodes > NodeBudget)
            throw new InvalidOperationException("solver returned an unsound certificate");
        if (result.IsOptimal && result.LowerBound != result.UpperBound)
            throw new InvalidOperationException("optimal result retained an open bound gap");
    }

    private static CaseData CreateDatabase(int[][] facts, int candidateCount)
    {
        YatagarasuDatabase database = YatagarasuDatabase.CreateInMemory();
        using (var write = database.BeginWriteTransaction())
        {
            VertexId[] candidates = Enumerable.Range(0, candidateCount)
                .Select(_ => write.CreateVertex("Candidate"))
                .ToArray();
            VertexId scope = write.CreateVertex("Scope");
            foreach (int[] fact in facts)
            {
                NexusMember[] members = fact.Distinct().Select(candidate => new NexusMember("candidate", candidates[candidate]))
                    .Append(new("scope", scope))
                    .ToArray();
                write.CreateNexus("Constraint", members);
            }
            write.Commit();
        }
        return new(database, database.BeginReadTransaction());
    }

    private static bool CanRunNext(
        StageReport previous,
        int nextFacts,
        int nextCandidates,
        Stopwatch total,
        out Projection projection)
    {
        double scale = Math.Max((double)nextFacts / previous.FactCount, (double)nextCandidates / previous.CandidateCount);
        double projectedMilliseconds = previous.Cases.Max(static report => report.MedianMilliseconds) * scale * 2;
        long projectedAllocation = (long)Math.Ceiling(previous.Cases.Max(static report => report.AllocatedBytes) * scale * 2);
        double projectedTotal = total.Elapsed.TotalMilliseconds + projectedMilliseconds * 4;
        bool safe = projectedMilliseconds <= CaseLimitMilliseconds
            && projectedAllocation <= CaseAllocationLimit
            && projectedTotal <= TotalLimit.TotalMilliseconds;
        projection = new(previous.FactCount, previous.CandidateCount, nextFacts, nextCandidates,
            projectedMilliseconds, projectedAllocation, projectedTotal);
        return safe;
    }

    private static void PrintProjection(Projection projection, string decision)
    {
        Console.WriteLine(
            $"minimum-hitting-set-projection fromFacts={projection.FromFacts} fromCandidates={projection.FromCandidates} " +
            $"toFacts={projection.ToFacts} toCandidates={projection.ToCandidates} " +
            $"projectedCaseMs={projection.ProjectedCaseMilliseconds:F3} " +
            $"projectedAllocationBytes={projection.ProjectedAllocationBytes} " +
            $"projectedTotalMs={projection.ProjectedTotalMilliseconds:F3} decision={decision}");
    }

    private static int[][] CreateDominated(int factCount, int candidateCount)
    {
        const int groups = 4;
        var facts = new List<int[]>(factCount);
        for (int group = 0; group < groups; group++) facts.Add([group * 2, group * 2 + 1]);
        int extra = 8;
        while (facts.Count < factCount)
        {
            int group = facts.Count % groups;
            var fact = new List<int> { group * 2, group * 2 + 1 };
            if (extra < candidateCount) fact.Add(extra++);
            fact.Add(8 + facts.Count % Math.Max(1, candidateCount - 8));
            facts.Add(fact.Distinct().Order().ToArray());
        }
        return facts.ToArray();
    }

    private static int[][] CreateSymmetric(int blocks, int blockSize, int duplicateFacts)
    {
        var facts = new List<int[]>();
        for (int block = 0; block < blocks; block++)
        {
            int start = block * blockSize;
            for (int left = 0; left < blockSize; left++)
                for (int right = left + 1; right < blockSize; right++) facts.Add([start + left, start + right]);
        }
        for (int i = 0; i < duplicateFacts; i++) facts.Add((int[])facts[i % facts.Count].Clone());
        return facts.ToArray();
    }

    private static int[][] CreatePackingAnchor(int factCount, int candidateCount)
    {
        int groups = candidateCount / 4;
        var facts = new List<int[]>(factCount);
        for (int group = 0; group < groups; group++) facts.Add(Enumerable.Range(group * 4, 4).ToArray());
        while (facts.Count < factCount)
        {
            int group = facts.Count % groups;
            int other = (group + 1 + facts.Count / groups) % groups;
            facts.Add(Enumerable.Range(group * 4, 4).Concat(Enumerable.Range(other * 4, 4)).ToArray());
        }
        return facts.ToArray();
    }

    private static int[][] CreateUniformHard(int factCount, int candidateCount, int seed)
    {
        var random = new Random(seed);
        int width = Math.Max(4, candidateCount / 8);
        var facts = new List<int[]>(factCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (facts.Count < factCount)
        {
            int[] fact = Enumerable.Range(0, candidateCount).OrderBy(_ => random.Next()).Take(width).Order().ToArray();
            if (seen.Add(string.Join(',', fact))) facts.Add(fact);
        }
        return facts.ToArray();
    }

    private sealed record CaseReport(
        double MedianMilliseconds,
        double SpreadPercent,
        long AllocatedBytes,
        MinimumHittingSetResult Result);

    private sealed record StageReport(int FactCount, int CandidateCount, List<CaseReport> Cases);

    private readonly record struct Projection(
        int FromFacts,
        int FromCandidates,
        int ToFacts,
        int ToCandidates,
        double ProjectedCaseMilliseconds,
        long ProjectedAllocationBytes,
        double ProjectedTotalMilliseconds);

    private sealed class CaseData(YatagarasuDatabase database, IReadTransaction read) : IDisposable
    {
        internal IReadTransaction Read { get; } = read;

        public void Dispose()
        {
            Read.Dispose();
            database.Dispose();
        }
    }
}
