using System.Diagnostics;
using System.Numerics;
using FluentAssertions;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class MinimumHittingSetAlgorithmsTests
{
    private static readonly MinimumHittingSetOptions Unlimited = new()
    {
        MaxNodes = long.MaxValue,
        TimeLimit = Timeout.InfiniteTimeSpan,
    };

    [Fact]
    public void Fixed_and_random_small_cases_match_the_bitmask_oracle()
    {
        var fixtures = new VertexId[][][]
        {
            [],
            [[]],
            [[Id(1)]],
            [[Id(0), Id(1)], [Id(0), Id(1)], [Id(0), Id(1), Id(2)]],
            [[Id(0), Id(1)], [Id(0), Id(1), Id(2)], [Id(0), Id(1), Id(3)]],
            [[Id(0), Id(1)], [Id(0), Id(1), Id(2)], [Id(0), Id(1), Id(2), Id(3)], [Id(3), Id(4)]],
            [[Id(0), Id(1)], [Id(0), Id(1)], [Id(0), Id(2)], [Id(0), Id(2)], [Id(1), Id(3)], [Id(2), Id(4)]],
            [[Id(0), Id(1)], [Id(2), Id(3)], [Id(4), Id(5)], [Id(6), Id(7)], [Id(0), Id(2), Id(4), Id(6)]],
        };
        foreach (VertexId[][] facts in fixtures) AssertMatchesOracle(facts);

        foreach (int seed in new[] { 0x4D5431, 0x4D5432, 0x4D5433 })
        {
            var random = new Random(seed);
            for (int trial = 0; trial < 24; trial++)
            {
                int candidateCount = random.Next(4, 13);
                int factCount = random.Next(1, 15);
                var facts = new VertexId[factCount][];
                for (int fact = 0; fact < factCount; fact++)
                {
                    int width = random.Next(1, Math.Min(6, candidateCount) + 1);
                    facts[fact] = Enumerable.Range(0, candidateCount)
                        .OrderBy(_ => random.Next())
                        .Take(width)
                        .Order()
                        .Select(Id)
                        .ToArray();
                }
                AssertMatchesOracle(facts);
            }
        }
    }

    [Fact]
    public void Greedy_trap_is_solved_exactly()
    {
        VertexId[][] facts =
        [
            [Id(0), Id(1)], [Id(0), Id(1)], [Id(0), Id(2)],
            [Id(0), Id(2)], [Id(1), Id(3)], [Id(2), Id(4)],
        ];

        MinimumHittingSetResult result = MinimumHittingSetAlgorithms.Solve(facts, Unlimited);

        result.IsOptimal.Should().BeTrue();
        result.UpperBound.Should().Be(2);
        CoversAll(facts, result.Solution).Should().BeTrue();
    }

    [Fact]
    public void Dominance_and_packing_reduce_the_search_without_changing_the_certificate()
    {
        VertexId[][] dominated =
        [
            [Id(0), Id(1)],
            [Id(0), Id(1), Id(2)],
            [Id(0), Id(1), Id(3)],
        ];
        MinimumHittingSetResult reduced = MinimumHittingSetAlgorithms.Solve(dominated, Unlimited);
        reduced.RemovedFactCount.Should().BeGreaterThan(0);
        reduced.RemovedCandidateCount.Should().BeGreaterThan(0);
        reduced.UpperBound.Should().Be(1);

        VertexId[][] packing =
        [
            [Id(0), Id(1)], [Id(2), Id(3)], [Id(4), Id(5)], [Id(6), Id(7)],
            [Id(0), Id(2), Id(4), Id(6)], [Id(1), Id(3), Id(5), Id(7)],
        ];
        MinimumHittingSetResult packed = MinimumHittingSetAlgorithms.Solve(packing, Unlimited);
        packed.LowerBound.Should().Be(4);
        packed.UpperBound.Should().Be(4);
        packed.VisitedNodes.Should().Be(0);
    }

    [Fact]
    public void Empty_input_empty_fact_and_single_fact_have_distinct_results()
    {
        MinimumHittingSetResult empty = MinimumHittingSetAlgorithms.Solve([], Unlimited);
        empty.HasSolution.Should().BeTrue();
        empty.Solution.Should().BeEmpty();
        empty.LowerBound.Should().Be(0);
        empty.UpperBound.Should().Be(0);
        empty.TerminationReason.Should().Be(MinimumHittingSetTerminationReason.Optimal);

        MinimumHittingSetResult infeasible = MinimumHittingSetAlgorithms.Solve([[]], Unlimited);
        infeasible.HasSolution.Should().BeFalse();
        infeasible.IsOptimal.Should().BeTrue();
        infeasible.LowerBound.Should().BeNull();
        infeasible.UpperBound.Should().BeNull();
        infeasible.TerminationReason.Should().Be(MinimumHittingSetTerminationReason.Infeasible);

        MinimumHittingSetResult singleton = MinimumHittingSetAlgorithms.Solve([[Id(7), Id(9)]], Unlimited);
        singleton.HasSolution.Should().BeTrue();
        singleton.Solution.Should().Equal(Id(7));
    }

    [Fact]
    public void Node_time_and_cancellation_budgets_keep_a_sound_feasible_certificate()
    {
        VertexId[][] hard = CreateSymmetric(blocks: 2, blockSize: 5, duplicateFacts: 0);
        int optimum = Oracle(hard).Optimum;

        var nodeOptions = new MinimumHittingSetOptions
        {
            MaxNodes = 1,
            TimeLimit = Timeout.InfiniteTimeSpan,
        };
        MinimumHittingSetResult first = MinimumHittingSetAlgorithms.Solve(hard, nodeOptions);
        MinimumHittingSetResult second = MinimumHittingSetAlgorithms.Solve(hard, nodeOptions);
        first.TerminationReason.Should().Be(MinimumHittingSetTerminationReason.NodeBudget);
        first.VisitedNodes.Should().BeLessThanOrEqualTo(1);
        first.Solution.Should().Equal(second.Solution);
        first.LowerBound.Should().Be(second.LowerBound);
        first.UpperBound.Should().Be(second.UpperBound);
        AssertCertificate(hard, optimum, first);

        long start = Stopwatch.GetTimestamp();
        MinimumHittingSetResult timed = MinimumHittingSetAlgorithms.Solve(hard, new()
        {
            MaxNodes = long.MaxValue,
            TimeLimit = TimeSpan.Zero,
        });
        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromMilliseconds(100));
        timed.TerminationReason.Should().Be(MinimumHittingSetTerminationReason.TimeBudget);
        timed.VisitedNodes.Should().Be(0);
        timed.LowerBound.Should().Be(1);
        AssertCertificate(hard, optimum, timed);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        start = Stopwatch.GetTimestamp();
        MinimumHittingSetResult cancelled = MinimumHittingSetAlgorithms.Solve(hard, new()
        {
            MaxNodes = long.MaxValue,
            TimeLimit = Timeout.InfiniteTimeSpan,
            CancellationToken = cancellation.Token,
        });
        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromMilliseconds(100));
        cancelled.TerminationReason.Should().Be(MinimumHittingSetTerminationReason.Cancelled);
        cancelled.VisitedNodes.Should().Be(0);
        cancelled.LowerBound.Should().Be(1);
        AssertCertificate(hard, optimum, cancelled);
    }

    [Fact]
    public void Zero_node_budget_returns_the_prepared_fallback_before_greedy()
    {
        VertexId[][] facts =
        [
            [Id(0), Id(3)],
            [Id(1), Id(3)],
            [Id(2), Id(3)],
        ];

        MinimumHittingSetResult result = MinimumHittingSetAlgorithms.Solve(facts, new()
        {
            MaxNodes = 0,
            TimeLimit = Timeout.InfiniteTimeSpan,
        });

        result.TerminationReason.Should().Be(MinimumHittingSetTerminationReason.NodeBudget);
        result.VisitedNodes.Should().Be(0);
        result.Solution.Should().Equal(Id(0), Id(1), Id(2));
        result.LowerBound.Should().Be(1);
        result.UpperBound.Should().Be(3);
        CoversAll(facts, result.Solution).Should().BeTrue();
    }

    [Fact]
    public void Budgeted_random_certificates_enclose_the_oracle_optimum()
    {
        var random = new Random(0x43455254);
        for (int trial = 0; trial < 32; trial++)
        {
            int candidateCount = random.Next(5, 13);
            VertexId[][] facts = Enumerable.Range(0, random.Next(3, 15))
                .Select(_ => Enumerable.Range(0, candidateCount)
                    .OrderBy(__ => random.Next())
                    .Take(random.Next(1, Math.Min(5, candidateCount) + 1))
                    .Select(Id)
                    .ToArray())
                .ToArray();
            int optimum = Oracle(facts).Optimum;
            MinimumHittingSetResult result = MinimumHittingSetAlgorithms.Solve(facts, new()
            {
                MaxNodes = trial % 5,
                TimeLimit = Timeout.InfiniteTimeSpan,
            });
            AssertCertificate(facts, optimum, result);
            if (result.IsOptimal)
                result.LowerBound.Should().Be(result.UpperBound);
        }
    }

    [Fact]
    public void Nexus_adapter_uses_the_read_transaction_snapshot()
    {
        using var db = YatagarasuDatabase.CreateInMemory();
        VertexId a, b, c, scope;
        using (var write = db.BeginWriteTransaction())
        {
            a = write.CreateVertex("Candidate");
            b = write.CreateVertex("Candidate");
            c = write.CreateVertex("Candidate");
            scope = write.CreateVertex("Scope");
            AddFact(write, scope, a, b);
            AddFact(write, scope, b, c);
            write.CreateNexus("Other", [new("candidate", a), new("scope", scope)]);
            write.Commit();
        }

        using var oldRead = db.BeginReadTransaction();
        using (var write = db.BeginWriteTransaction())
        {
            AddFact(write, scope, a, c);
            using var uncommittedRead = db.BeginReadTransaction();
            uncommittedRead.FindMinimumHittingSet("Constraint", "candidate", Unlimited)
                .Solution.Should().Equal(b);
            write.Commit();
        }

        oldRead.FindMinimumHittingSet("Constraint", "candidate", Unlimited)
            .Solution.Should().Equal(b);
        using var currentRead = db.BeginReadTransaction();
        MinimumHittingSetResult current = currentRead.FindMinimumHittingSet(
            "Constraint", "candidate", Unlimited);
        current.IsOptimal.Should().BeTrue();
        current.UpperBound.Should().Be(2);
        current.Solution.Should().Equal(a, b);
    }

    [Fact]
    public void Nexus_without_the_cover_role_is_reported_as_infeasible()
    {
        using var db = YatagarasuDatabase.CreateInMemory();
        using (var write = db.BeginWriteTransaction())
        {
            VertexId left = write.CreateVertex("Item");
            VertexId right = write.CreateVertex("Item");
            write.CreateNexus("Constraint", [new("left", left), new("right", right)]);
            write.Commit();
        }
        using var read = db.BeginReadTransaction();
        MinimumHittingSetResult result = read.FindMinimumHittingSet("Constraint", "candidate", Unlimited);
        result.HasSolution.Should().BeFalse();
        result.TerminationReason.Should().Be(MinimumHittingSetTerminationReason.Infeasible);
    }

    private static void AssertMatchesOracle(VertexId[][] facts)
    {
        OracleResult oracle = Oracle(facts);
        MinimumHittingSetResult result = MinimumHittingSetAlgorithms.Solve(facts, Unlimited);
        result.HasSolution.Should().Be(oracle.HasSolution);
        result.IsOptimal.Should().BeTrue();
        if (!oracle.HasSolution)
        {
            result.TerminationReason.Should().Be(MinimumHittingSetTerminationReason.Infeasible);
            return;
        }
        result.LowerBound.Should().Be(oracle.Optimum);
        result.UpperBound.Should().Be(oracle.Optimum);
        CoversAll(facts, result.Solution).Should().BeTrue();
    }

    private static void AssertCertificate(VertexId[][] facts, int optimum, MinimumHittingSetResult result)
    {
        result.HasSolution.Should().BeTrue();
        CoversAll(facts, result.Solution).Should().BeTrue();
        result.LowerBound.Should().BeLessThanOrEqualTo(optimum);
        result.UpperBound.Should().BeGreaterThanOrEqualTo(optimum);
        result.UpperBound.Should().Be(result.Solution.Count);
    }

    private static OracleResult Oracle(VertexId[][] facts)
    {
        VertexId[] candidates = facts.SelectMany(static fact => fact).Distinct().OrderBy(static id => id.Value).ToArray();
        if (candidates.Length > 16) throw new ArgumentOutOfRangeException(nameof(facts));
        int best = int.MaxValue;
        ulong limit = 1UL << candidates.Length;
        for (ulong mask = 0; mask < limit; mask++)
        {
            int size = BitOperations.PopCount(mask);
            if (size >= best) continue;
            bool covered = facts.All(fact => fact.Any(candidate =>
            {
                int index = Array.IndexOf(candidates, candidate);
                return (mask & (1UL << index)) != 0;
            }));
            if (covered) best = size;
        }
        return best == int.MaxValue ? new(false, 0) : new(true, best);
    }

    private static bool CoversAll(VertexId[][] facts, IReadOnlyList<VertexId> solution)
        => facts.All(fact => fact.Any(solution.Contains));

    private static VertexId[][] CreateSymmetric(int blocks, int blockSize, int duplicateFacts)
    {
        var facts = new List<VertexId[]>();
        for (int block = 0; block < blocks; block++)
        {
            int start = block * blockSize;
            for (int left = 0; left < blockSize; left++)
                for (int right = left + 1; right < blockSize; right++)
                    facts.Add([Id(start + left), Id(start + right)]);
        }
        for (int i = 0; i < duplicateFacts; i++) facts.Add((VertexId[])facts[i % facts.Count].Clone());
        return facts.ToArray();
    }

    private static void AddFact(IWriteTransaction write, VertexId scope, params VertexId[] candidates)
    {
        NexusMember[] members = candidates.Select(candidate => new NexusMember("candidate", candidate))
            .Append(new("scope", scope))
            .ToArray();
        write.CreateNexus("Constraint", members);
    }

    private static VertexId Id(int value) => VertexId.Create(value, 1);

    private readonly record struct OracleResult(bool HasSolution, int Optimum);
}
