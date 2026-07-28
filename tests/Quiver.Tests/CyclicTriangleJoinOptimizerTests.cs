using FluentAssertions;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Query.Optimizer;
using Xunit;

namespace Quiver.Tests;

public sealed class CyclicTriangleJoinOptimizerTests : IDisposable
{
    private readonly string _directory;
    private readonly QuiverDatabase _database;

    public CyclicTriangleJoinOptimizerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "quiver_triangle_join_" + Guid.NewGuid().ToString("N"));
        _database = QuiverDatabase.Open(Path.Combine(_directory, "graph.quiver"));
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Dense_product_relations_use_transient_columns_and_match_independent_oracle()
    {
        SeedAdversarialTriangle(32);
        Relations relations = ExtractRelations();

        CyclicTriangleJoinResult result = CyclicTriangleJoinOptimizer.Execute(
            relations.FirstToSecond, relations.SecondToThird, relations.ThirdToFirst);

        result.Strategy.Should().Be(CyclicTriangleJoinStrategy.TransientColumns);
        result.RouteReason.Should().Be(CyclicTriangleJoinRouteReason.DenseCyclic);
        result.IsComplete.Should().BeTrue();
        result.Rows.Should().Equal(Oracle(relations));
        result.PeakWorkingRows.Should().BeLessThan(2 * 32 + 1);
    }

    [Fact]
    public void Small_cycle_uses_materializing_fallback()
    {
        Relations relations = DenseRelations(8);

        CyclicTriangleJoinResult result = CyclicTriangleJoinOptimizer.Execute(
            relations.FirstToSecond, relations.SecondToThird, relations.ThirdToFirst);

        result.Strategy.Should().Be(CyclicTriangleJoinStrategy.Materializing);
        result.RouteReason.Should().Be(CyclicTriangleJoinRouteReason.IntermediateTooSmall);
        result.Rows.Should().Equal(Oracle(relations));
    }

    [Fact]
    public void Route_threshold_is_deterministic_at_the_dense_boundary()
    {
        Relations below = DenseRelations(31);
        Relations boundary = DenseRelations(32);

        CyclicTriangleJoinResult belowResult = CyclicTriangleJoinOptimizer.Execute(
            below.FirstToSecond, below.SecondToThird, below.ThirdToFirst);
        CyclicTriangleJoinResult boundaryResult = CyclicTriangleJoinOptimizer.Execute(
            boundary.FirstToSecond, boundary.SecondToThird, boundary.ThirdToFirst);

        belowResult.EstimatedIntermediateRows.Should().Be(29_791);
        belowResult.Strategy.Should().Be(CyclicTriangleJoinStrategy.Materializing);
        boundaryResult.EstimatedIntermediateRows.Should().Be(CyclicTriangleJoinOptimizer.MinimumMaterializedIntermediateRows);
        boundaryResult.Strategy.Should().Be(CyclicTriangleJoinStrategy.TransientColumns);
    }

    [Fact]
    public void Duplicate_pairs_fall_back_and_preserve_bag_multiplicity()
    {
        var first = new[] { Pair(1, 2), Pair(1, 2) };
        var second = new[] { Pair(2, 3), Pair(2, 3), Pair(2, 3) };
        var closing = new[] { Pair(3, 1), Pair(3, 1) };

        CyclicTriangleJoinResult result = CyclicTriangleJoinOptimizer.Execute(first, second, closing);

        result.Strategy.Should().Be(CyclicTriangleJoinStrategy.Materializing);
        result.RouteReason.Should().Be(CyclicTriangleJoinRouteReason.DuplicatePair);
        result.Rows.Should().HaveCount(12);
        result.Rows.Should().Equal(Oracle(new(first, second, closing)));
    }

    [Fact]
    public void Empty_relation_completes_without_building_a_join_path()
    {
        CyclicTriangleJoinResult result = CyclicTriangleJoinOptimizer.Execute(
            Array.Empty<CyclicTriangleJoinPair>(), [Pair(2, 3)], [Pair(3, 1)]);

        result.Strategy.Should().Be(CyclicTriangleJoinStrategy.Empty);
        result.RouteReason.Should().Be(CyclicTriangleJoinRouteReason.EmptyRelation);
        result.IsComplete.Should().BeTrue();
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Shuffled_dense_inputs_keep_lexicographic_output_order()
    {
        Relations relations = DenseRelations(32);
        Relations reversed = new(
            relations.FirstToSecond.Reverse().ToArray(),
            relations.SecondToThird.Reverse().ToArray(),
            relations.ThirdToFirst.Reverse().ToArray());

        CyclicTriangleJoinResult expected = CyclicTriangleJoinOptimizer.Execute(
            relations.FirstToSecond, relations.SecondToThird, relations.ThirdToFirst);
        CyclicTriangleJoinResult actual = CyclicTriangleJoinOptimizer.Execute(
            reversed.FirstToSecond, reversed.SecondToThird, reversed.ThirdToFirst);

        actual.Rows.Should().Equal(expected.Rows);
    }

    [Fact]
    public void Result_work_time_and_intermediate_limits_are_bounded()
    {
        Relations dense = DenseRelations(32);
        CyclicTriangleJoinResult full = CyclicTriangleJoinOptimizer.Execute(
            dense.FirstToSecond, dense.SecondToThird, dense.ThirdToFirst);
        CyclicTriangleJoinResult resultLimited = CyclicTriangleJoinOptimizer.Execute(
            dense.FirstToSecond, dense.SecondToThird, dense.ThirdToFirst,
            new CyclicTriangleJoinOptions { MaxResults = 7 });
        CyclicTriangleJoinResult workLimited = CyclicTriangleJoinOptimizer.Execute(
            dense.FirstToSecond, dense.SecondToThird, dense.ThirdToFirst,
            new CyclicTriangleJoinOptions { MaxWork = 1 });
        CyclicTriangleJoinResult timeLimited = CyclicTriangleJoinOptimizer.Execute(
            dense.FirstToSecond, dense.SecondToThird, dense.ThirdToFirst,
            new CyclicTriangleJoinOptions { TimeLimit = TimeSpan.Zero });

        resultLimited.TerminationReason.Should().Be(CyclicTriangleJoinTerminationReason.MaxResultsReached);
        resultLimited.Rows.Should().Equal(full.Rows.Take(7));
        workLimited.TerminationReason.Should().Be(CyclicTriangleJoinTerminationReason.WorkBudgetReached);
        workLimited.Work.Should().Be(1);
        timeLimited.TerminationReason.Should().Be(CyclicTriangleJoinTerminationReason.TimeBudgetReached);

        Relations small = DenseRelations(4);
        CyclicTriangleJoinResult intermediateLimited = CyclicTriangleJoinOptimizer.Execute(
            small.FirstToSecond, small.SecondToThird, small.ThirdToFirst,
            new CyclicTriangleJoinOptions { MaxMaterializedIntermediateRows = 63 });
        intermediateLimited.TerminationReason.Should().Be(CyclicTriangleJoinTerminationReason.IntermediateLimitReached);
        intermediateLimited.Rows.Should().BeEmpty();

        CyclicTriangleJoinResult exactBoundary = CyclicTriangleJoinOptimizer.Execute(
            [Pair(1, 2)], [Pair(2, 3)], [Pair(3, 1)],
            new CyclicTriangleJoinOptions { MaxResults = 1, MaxWork = 2 });
        exactBoundary.TerminationReason.Should().Be(CyclicTriangleJoinTerminationReason.Completed);
        exactBoundary.Rows.Should().ContainSingle();
    }

    [Fact]
    public void Cancellation_is_observed_before_execution()
    {
        Relations relations = DenseRelations(32);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Action act = () => CyclicTriangleJoinOptimizer.Execute(
            relations.FirstToSecond, relations.SecondToThird, relations.ThirdToFirst,
            new CyclicTriangleJoinOptions { CancellationToken = cancellation.Token });

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Input_limit_rejects_relations_before_route_analysis()
    {
        Relations relations = DenseRelations(8);

        CyclicTriangleJoinResult result = CyclicTriangleJoinOptimizer.Execute(
            relations.FirstToSecond, relations.SecondToThird, relations.ThirdToFirst,
            new CyclicTriangleJoinOptions { MaxInputRows = 135 });

        result.Strategy.Should().Be(CyclicTriangleJoinStrategy.Empty);
        result.RouteReason.Should().Be(CyclicTriangleJoinRouteReason.InputLimit);
        result.TerminationReason.Should().Be(CyclicTriangleJoinTerminationReason.InputLimitReached);
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Existing_acyclic_match_path_is_unchanged()
    {
        using (var write = _database.BeginWriteTransaction())
        {
            VertexId a = write.CreateVertex("A");
            VertexId b = write.CreateVertex("B");
            VertexId c = write.CreateVertex("C");
            write.CreateEdge(a, b, "R");
            write.CreateEdge(b, c, "S");
            write.Commit();
        }

        using var read = _database.BeginReadTransaction();
        long firstHop = read.Query.Match(
            GraphPattern.Vertex("a", "A").Out("R", GraphPattern.Vertex("b", "B"))).Count();
        long secondHop = read.Query.Match(
            GraphPattern.Vertex("b", "B").Out("S", GraphPattern.Vertex("c", "C"))).Count();

        firstHop.Should().Be(1);
        secondHop.Should().Be(1);
    }

    [Fact]
    public void Random_small_cases_match_independent_bag_oracle()
    {
        var random = new Random(0x7103);
        for (int sample = 0; sample < 40; sample++)
        {
            CyclicTriangleJoinPair[] first = RandomRelation(random, 5, 18);
            CyclicTriangleJoinPair[] second = RandomRelation(random, 5, 18, leftOffset: 100, rightOffset: 200);
            CyclicTriangleJoinPair[] closing = RandomRelation(random, 5, 18, leftOffset: 200, rightOffset: 0);
            for (int i = 0; i < first.Length; i++)
                first[i] = new(first[i].Left, new VertexId(first[i].Right.Value + 100));

            var relations = new Relations(first, second, closing);
            CyclicTriangleJoinResult result = CyclicTriangleJoinOptimizer.Execute(first, second, closing);
            result.Rows.Should().Equal(Oracle(relations));
        }
    }

    private void SeedAdversarialTriangle(int domain)
    {
        using var write = _database.BeginWriteTransaction();
        VertexId[] first = CreateVertices(write, "A", domain);
        VertexId[] second = CreateVertices(write, "B", domain);
        VertexId[] third = CreateVertices(write, "C", domain);
        for (int i = 0; i < domain; i++)
        {
            for (int j = 0; j < domain; j++)
            {
                write.CreateEdge(first[i], second[j], "R");
                write.CreateEdge(second[i], third[j], "S");
            }
            write.CreateEdge(third[i], first[i], "T");
        }
        write.Commit();
    }

    private Relations ExtractRelations()
    {
        using var read = _database.BeginReadTransaction();
        List<CyclicTriangleJoinPair> first = read.Query.Match(
                GraphPattern.Vertex("a", "A").Out("R", GraphPattern.Vertex("b", "B")))
            .Return(ctx => new CyclicTriangleJoinPair(ctx.Vertex("a"), ctx.Vertex("b"))).ToList();
        List<CyclicTriangleJoinPair> second = read.Query.Match(
                GraphPattern.Vertex("b", "B").Out("S", GraphPattern.Vertex("c", "C")))
            .Return(ctx => new CyclicTriangleJoinPair(ctx.Vertex("b"), ctx.Vertex("c"))).ToList();
        List<CyclicTriangleJoinPair> closing = read.Query.Match(
                GraphPattern.Vertex("c", "C").Out("T", GraphPattern.Vertex("a", "A")))
            .Return(ctx => new CyclicTriangleJoinPair(ctx.Vertex("c"), ctx.Vertex("a"))).ToList();
        return new(first, second, closing);
    }

    private static Relations DenseRelations(int domain)
    {
        var first = new List<CyclicTriangleJoinPair>(domain * domain);
        var second = new List<CyclicTriangleJoinPair>(domain * domain);
        var closing = new List<CyclicTriangleJoinPair>(domain);
        for (int i = 0; i < domain; i++)
        {
            for (int j = 0; j < domain; j++)
            {
                first.Add(new(A(i), B(j)));
                second.Add(new(B(i), C(j)));
            }
            closing.Add(new(C(i), A(i)));
        }
        return new(first, second, closing);
    }

    private static CyclicTriangleJoinRow[] Oracle(Relations relations)
    {
        var rows = new List<CyclicTriangleJoinRow>();
        foreach (CyclicTriangleJoinPair first in relations.FirstToSecond)
        foreach (CyclicTriangleJoinPair second in relations.SecondToThird)
        {
            if (first.Right != second.Left) continue;
            foreach (CyclicTriangleJoinPair closing in relations.ThirdToFirst)
            {
                if (second.Right == closing.Left && closing.Right == first.Left)
                    rows.Add(new(first.Left, first.Right, second.Right));
            }
        }
        rows.Sort(static (left, right) =>
        {
            int first = left.First.Value.CompareTo(right.First.Value);
            if (first != 0) return first;
            int second = left.Second.Value.CompareTo(right.Second.Value);
            return second != 0 ? second : left.Third.Value.CompareTo(right.Third.Value);
        });
        return rows.ToArray();
    }

    private static CyclicTriangleJoinPair[] RandomRelation(
        Random random, int domain, int count, int leftOffset = 0, int rightOffset = 0)
    {
        var rows = new CyclicTriangleJoinPair[count];
        for (int i = 0; i < count; i++)
            rows[i] = Pair(leftOffset + random.Next(domain), rightOffset + random.Next(domain));
        return rows;
    }

    private static VertexId[] CreateVertices(IWriteTransaction write, string label, int count)
    {
        var vertices = new VertexId[count];
        for (int i = 0; i < count; i++) vertices[i] = write.CreateVertex(label);
        return vertices;
    }

    private static CyclicTriangleJoinPair Pair(long left, long right) => new(new VertexId(left), new VertexId(right));
    private static VertexId A(int value) => new(1_000 + value);
    private static VertexId B(int value) => new(2_000 + value);
    private static VertexId C(int value) => new(3_000 + value);

    private readonly record struct Relations(
        IReadOnlyList<CyclicTriangleJoinPair> FirstToSecond,
        IReadOnlyList<CyclicTriangleJoinPair> SecondToThird,
        IReadOnlyList<CyclicTriangleJoinPair> ThirdToFirst);
}
