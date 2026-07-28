using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class GraphAnnotationAlgorithmsTests
{
    private static readonly GraphAnnotationOptions Unlimited = new()
    {
        MaxVertices = 10_000,
        MaxEdges = 100_000,
        MaxResults = 10_000,
        MaxRelaxations = 1_000_000,
        MaxAnnotationUpdates = 1_000_000,
        TimeLimit = Timeout.InfiniteTimeSpan,
    };

    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    public void Built_in_policies_match_independent_oracles(int seed)
    {
        const int vertexCount = 12;
        Arc[] dag = CreateDag(vertexCount, seed, allowNegative: false);
        Arc[] cyclic = [.. dag, new(vertexCount - 1, 0, 0.75)];
        Arc[] negativeDag = CreateDag(vertexCount, seed ^ 0x5a5a, allowNegative: true);

        using (GraphCase graph = CreateGraph(vertexCount, cyclic))
        {
            GraphAnnotationResult<bool> actual = graph.Read.EvaluateReachability(
                graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst, Unlimited);
            AssertValues(actual, OracleBfs(vertexCount, cyclic), static value => value);
        }
        using (GraphCase graph = CreateGraph(vertexCount, cyclic))
        {
            GraphAnnotationResult<double> actual = graph.Read.EvaluateTropical(
                graph.Vertices[0], GraphAnnotationPolicy.LabelSetting, graph.SelectValue, Unlimited);
            AssertValues(actual, OracleDijkstra(vertexCount, cyclic));
        }
        using (GraphCase graph = CreateGraph(vertexCount, dag))
        {
            GraphAnnotationResult<double> actual = graph.Read.EvaluateTropical(
                graph.Vertices[0], GraphAnnotationPolicy.AcyclicDynamicProgramming, graph.SelectValue, Unlimited);
            AssertValues(actual, OracleDagTropical(vertexCount, dag));
        }
        using (GraphCase graph = CreateGraph(vertexCount, dag.Select(static edge => edge with { Value = 0.1 + edge.Value % 0.9 }).ToArray()))
        {
            double[] probabilities = graph.Arcs.Select(static edge => edge.Value).ToArray();
            GraphAnnotationResult<double> actual = graph.Read.EvaluateViterbi(
                graph.Vertices[0], GraphAnnotationPolicy.AcyclicDynamicProgramming, graph.SelectValue, Unlimited);
            AssertValues(actual, OracleViterbi(vertexCount, graph.Arcs));
            probabilities.Should().OnlyContain(static value => value >= 0 && value <= 1);
        }
        using (GraphCase graph = CreateGraph(vertexCount, negativeDag))
        {
            GraphAnnotationResult<double> actual = graph.Read.EvaluateTropical(
                graph.Vertices[0], GraphAnnotationPolicy.BoundedWorklist, graph.SelectValue, Unlimited);
            AssertValues(actual, OracleBellmanFord(vertexCount, negativeDag));
        }
    }

    [Fact]
    public void Policy_preconditions_are_rejected_before_relaxation()
    {
        using GraphCase negative = CreateGraph(2, [new(0, 1, -1)]);
        GraphAnnotationResult<double> labelSetting = negative.Read.EvaluateTropical(
            negative.Vertices[0], GraphAnnotationPolicy.LabelSetting, negative.SelectValue, Unlimited);
        labelSetting.TerminationReason.Should().Be(GraphAnnotationTerminationReason.NegativeEdgeRejected);
        labelSetting.RelaxationCount.Should().Be(0);

        using GraphCase cyclic = CreateGraph(2, [new(0, 1, 0.5), new(1, 0, 0.5)]);
        GraphAnnotationResult<double> dag = cyclic.Read.EvaluateViterbi(
            cyclic.Vertices[0], GraphAnnotationPolicy.AcyclicDynamicProgramming, cyclic.SelectValue, Unlimited);
        dag.TerminationReason.Should().Be(GraphAnnotationTerminationReason.CycleRejected);
        dag.RelaxationCount.Should().Be(0);

        GraphAnnotationResult<bool> wrong = cyclic.Read.EvaluateReachability(
            cyclic.Vertices[0], GraphAnnotationPolicy.LabelSetting, Unlimited);
        wrong.TerminationReason.Should().Be(GraphAnnotationTerminationReason.UnsupportedPolicyRejected);
        wrong.VertexCount.Should().Be(0);
    }

    [Fact]
    public void Undefined_policy_is_rejected_by_every_built_in_before_relaxation()
    {
        using GraphCase graph = CreateGraph(2, [new(0, 1, 0.5)]);
        var undefined = (GraphAnnotationPolicy)int.MaxValue;

        GraphAnnotationResult<bool> reachability = graph.Read.EvaluateReachability(
            graph.Vertices[0], undefined, Unlimited);
        GraphAnnotationResult<double> tropical = graph.Read.EvaluateTropical(
            graph.Vertices[0], undefined, graph.SelectValue, Unlimited);
        GraphAnnotationResult<double> viterbi = graph.Read.EvaluateViterbi(
            graph.Vertices[0], undefined, graph.SelectValue, Unlimited);
        GraphAnnotationResult<long> multiplicity = graph.Read.EvaluatePathMultiplicity(
            graph.Vertices[0], undefined, Unlimited);

        new GraphAnnotationTerminationReason[]
        {
            reachability.TerminationReason,
            tropical.TerminationReason,
            viterbi.TerminationReason,
            multiplicity.TerminationReason,
        }.Should().OnlyContain(static reason => reason == GraphAnnotationTerminationReason.UnsupportedPolicyRejected);
        new[]
        {
            reachability.RelaxationCount,
            tropical.RelaxationCount,
            viterbi.RelaxationCount,
            multiplicity.RelaxationCount,
        }.Should().OnlyContain(static count => count == 0);
    }

    [Fact]
    public void Null_transaction_is_validated_before_policy_rejection_by_every_built_in()
    {
        IReadTransaction transaction = null!;
        VertexId source = VertexId.Create(0, 1);
        var undefined = (GraphAnnotationPolicy)int.MaxValue;
        GraphEdgeValueSelector selector = static (_, _) => 1;

        Action reachability = () => transaction.EvaluateReachability(source, undefined, Unlimited);
        Action tropical = () => transaction.EvaluateTropical(source, undefined, selector, Unlimited);
        Action viterbi = () => transaction.EvaluateViterbi(source, undefined, selector, Unlimited);
        Action multiplicity = () => transaction.EvaluatePathMultiplicity(source, undefined, Unlimited);

        reachability.Should().Throw<ArgumentNullException>().WithParameterName("transaction");
        tropical.Should().Throw<ArgumentNullException>().WithParameterName("transaction");
        viterbi.Should().Throw<ArgumentNullException>().WithParameterName("transaction");
        multiplicity.Should().Throw<ArgumentNullException>().WithParameterName("transaction");
    }

    [Fact]
    public void Negative_cycle_and_annotation_growth_stop_at_their_budgets_without_partial_success()
    {
        using GraphCase negative = CreateGraph(4,
            [new(0, 1, 1), new(1, 2, -3), new(2, 3, 1), new(2, 1, 1)]);
        GraphAnnotationOptions relaxationBudget = WithOptions(Unlimited, maxRelaxations: 256);
        GraphAnnotationResult<double> tropical = negative.Read.EvaluateTropical(
            negative.Vertices[0], GraphAnnotationPolicy.BoundedWorklist, negative.SelectValue, relaxationBudget);
        tropical.TerminationReason.Should().Be(GraphAnnotationTerminationReason.RelaxationBudgetExceeded);
        tropical.RelaxationCount.Should().Be(256);
        tropical.QueuePeak.Should().Be(2);
        tropical.IsExact.Should().BeFalse();
        tropical.Annotations.Should().BeEmpty();

        using GraphCase growth = CreateGraph(4,
            [new(0, 1, 1), new(1, 2, 1), new(2, 1, 1), new(2, 3, 1)]);
        GraphAnnotationOptions annotationBudget = WithOptions(Unlimited, maxRelaxations: 1_000, maxAnnotationUpdates: 256);
        GraphAnnotationResult<long> multiplicity = growth.Read.EvaluatePathMultiplicity(
            growth.Vertices[0], GraphAnnotationPolicy.BoundedWorklist, annotationBudget);
        multiplicity.TerminationReason.Should().Be(GraphAnnotationTerminationReason.AnnotationBudgetExceeded);
        multiplicity.RelaxationCount.Should().Be(256);
        multiplicity.AnnotationUpdateCount.Should().Be(256);
        multiplicity.QueuePeak.Should().Be(2);
        multiplicity.IsComplete.Should().BeFalse();
        multiplicity.Annotations.Should().BeEmpty();
    }

    [Fact]
    public void Input_result_time_and_cancellation_limits_are_explicit()
    {
        using GraphCase graph = CreateGraph(4, [new(0, 1, 1), new(1, 2, 1), new(2, 3, 1)]);

        graph.Read.EvaluateReachability(graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst,
                WithOptions(Unlimited, maxVertices: 3))
            .TerminationReason.Should().Be(GraphAnnotationTerminationReason.MaxVerticesReached);
        graph.Read.EvaluateReachability(graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst,
                WithOptions(Unlimited, maxEdges: 2))
            .TerminationReason.Should().Be(GraphAnnotationTerminationReason.MaxEdgesReached);

        GraphAnnotationResult<bool> prefix = graph.Read.EvaluateReachability(
            graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst, WithOptions(Unlimited, maxResults: 2));
        prefix.TerminationReason.Should().Be(GraphAnnotationTerminationReason.MaxResultsReached);
        prefix.TotalAnnotationCount.Should().Be(4);
        prefix.Annotations.Select(static item => item.VertexId).Should().Equal(graph.Vertices.Take(2));
        prefix.IsExact.Should().BeFalse();

        graph.Read.EvaluateReachability(graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst,
                WithOptions(Unlimited, timeLimit: TimeSpan.Zero))
            .TerminationReason.Should().Be(GraphAnnotationTerminationReason.TimeLimitReached);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        graph.Read.EvaluateReachability(graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst,
                WithOptions(Unlimited, cancellationToken: cancelled.Token))
            .TerminationReason.Should().Be(GraphAnnotationTerminationReason.Cancelled);
    }

    [Fact]
    public void Count_limits_accept_exact_boundary_and_stop_before_one_extra_item()
    {
        using GraphCase graph = CreateGraph(4, [new(0, 1, 1), new(1, 2, 1), new(2, 3, 1)]);
        GraphAnnotationResult<bool> exact = graph.Read.EvaluateReachability(
            graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst,
            WithOptions(Unlimited, maxVertices: 4, maxEdges: 3, maxResults: 4));
        exact.TerminationReason.Should().Be(GraphAnnotationTerminationReason.Completed);
        exact.VertexCount.Should().Be(4);
        exact.EdgeCount.Should().Be(3);
        exact.Annotations.Should().HaveCount(4);

        graph.Read.EvaluateReachability(graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst,
                WithOptions(Unlimited, maxVertices: 3))
            .VertexCount.Should().Be(3);
        graph.Read.EvaluateReachability(graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst,
                WithOptions(Unlimited, maxEdges: 2))
            .EdgeCount.Should().Be(2);
        graph.Read.EvaluateReachability(graph.Vertices[0], GraphAnnotationPolicy.BreadthFirst,
                WithOptions(Unlimited, maxRelaxations: 0))
            .Should().Match<GraphAnnotationResult<bool>>(result =>
                result.TerminationReason == GraphAnnotationTerminationReason.RelaxationBudgetExceeded
                && result.RelaxationCount == 0);
    }

    [Fact]
    public void Edge_type_filter_and_result_order_are_deterministic()
    {
        using var database = QuiverDatabase.CreateInMemory();
        VertexId[] vertices;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            vertices = Enumerable.Range(0, 4).Select(_ => write.CreateVertex("Node")).ToArray();
            write.CreateEdge(vertices[0], vertices[3], "Keep");
            write.CreateEdge(vertices[0], vertices[2], "Drop");
            write.CreateEdge(vertices[0], vertices[1], "Keep");
            write.Commit();
        }
        using IReadTransaction read = database.BeginReadTransaction();
        GraphAnnotationResult<bool> first = read.EvaluateReachability(
            vertices[0], GraphAnnotationPolicy.BreadthFirst,
            WithOptions(Unlimited, edgeType: "Keep"));
        GraphAnnotationResult<bool> second = read.EvaluateReachability(
            vertices[0], GraphAnnotationPolicy.BreadthFirst,
            WithOptions(Unlimited, edgeType: "Keep"));
        first.Annotations.Select(static item => item.VertexId).Should().Equal(vertices[0], vertices[1], vertices[3]);
        second.Annotations.Should().Equal(first.Annotations);
        first.EdgeCount.Should().Be(2);
    }

    [Fact]
    public void Finite_path_multiplicity_overflow_is_reported_without_partial_values()
    {
        const int vertexCount = 66;
        Arc[] arcs = Enumerable.Range(0, vertexCount - 1)
            .SelectMany(static vertex => new[] { new Arc(vertex, vertex + 1, 1), new Arc(vertex, vertex + 1, 1) })
            .ToArray();
        using GraphCase graph = CreateGraph(vertexCount, arcs);
        GraphAnnotationResult<long> result = graph.Read.EvaluatePathMultiplicity(
            graph.Vertices[0], GraphAnnotationPolicy.BoundedWorklist, Unlimited);
        result.TerminationReason.Should().Be(GraphAnnotationTerminationReason.NumericOverflow);
        result.Annotations.Should().BeEmpty();
        result.IsExact.Should().BeFalse();
    }

    [Fact]
    public void Invalid_edge_values_and_provider_exceptions_have_fixed_boundaries()
    {
        using GraphCase graph = CreateGraph(2, [new(0, 1, 0.5)]);
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.01, 1.01 })
        {
            GraphAnnotationResult<double> result = graph.Read.EvaluateViterbi(
                graph.Vertices[0], GraphAnnotationPolicy.AcyclicDynamicProgramming, (_, _) => invalid, Unlimited);
            result.TerminationReason.Should().Be(GraphAnnotationTerminationReason.InvalidEdgeValueRejected);
            result.RelaxationCount.Should().Be(0);
        }

        Action act = () => graph.Read.EvaluateTropical(
            graph.Vertices[0], GraphAnnotationPolicy.LabelSetting,
            (_, _) => throw new TestProviderException(), Unlimited);
        act.Should().Throw<TestProviderException>();
    }

    [Fact]
    public void Empty_unreachable_missing_source_and_numeric_overflow_are_not_ambiguous()
    {
        using (var database = QuiverDatabase.CreateInMemory())
        using (IReadTransaction read = database.BeginReadTransaction())
        {
            GraphAnnotationResult<bool> empty = read.EvaluateReachability(
                VertexId.Create(0, 1), GraphAnnotationPolicy.BreadthFirst, Unlimited);
            empty.TerminationReason.Should().Be(GraphAnnotationTerminationReason.SourceNotFound);
            empty.Annotations.Should().BeEmpty();
        }

        using (GraphCase disconnected = CreateGraph(3, [new(0, 1, 1)]))
        {
            GraphAnnotationResult<double> result = disconnected.Read.EvaluateTropical(
                disconnected.Vertices[0], GraphAnnotationPolicy.LabelSetting, disconnected.SelectValue, Unlimited);
            result.Annotations.Select(static value => value.VertexId).Should().Equal(disconnected.Vertices[0], disconnected.Vertices[1]);
        }

        using (GraphCase overflow = CreateGraph(3, [new(0, 1, double.MaxValue), new(1, 2, double.MaxValue)]))
        {
            GraphAnnotationResult<double> result = overflow.Read.EvaluateTropical(
                overflow.Vertices[0], GraphAnnotationPolicy.AcyclicDynamicProgramming, overflow.SelectValue, Unlimited);
            result.TerminationReason.Should().Be(GraphAnnotationTerminationReason.NumericOverflow);
            result.Annotations.Should().BeEmpty();
        }
    }

    [Fact]
    public void Snapshot_adapter_excludes_later_commits()
    {
        using var database = QuiverDatabase.CreateInMemory();
        VertexId source;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            source = write.CreateVertex("Node");
            write.Commit();
        }
        using IReadTransaction oldRead = database.BeginReadTransaction();
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            VertexId later = write.CreateVertex("Node");
            write.CreateEdge(source, later, "Arc");
            write.Commit();
        }

        GraphAnnotationResult<bool> old = oldRead.EvaluateReachability(
            source, GraphAnnotationPolicy.BreadthFirst, Unlimited);
        old.IsExact.Should().BeTrue();
        old.VertexCount.Should().Be(1);
        old.EdgeCount.Should().Be(0);
    }

    private static void AssertValues(GraphAnnotationResult<double> actual, double[] expected)
    {
        actual.IsExact.Should().BeTrue();
        var values = actual.Annotations.ToDictionary(static item => item.VertexId, static item => item.Value);
        for (int i = 0; i < expected.Length; i++)
        {
            if (double.IsPositiveInfinity(expected[i]))
            {
                values.Should().NotContainKey(WithValue(actual.Annotations[0].VertexId, i));
                continue;
            }
            values[WithValue(actual.Annotations[0].VertexId, i)].Should().BeApproximately(expected[i], 1e-10);
        }
    }

    private static void AssertValues(GraphAnnotationResult<bool> actual, bool[] expected, Func<bool, bool> include)
    {
        actual.IsExact.Should().BeTrue();
        VertexId source = actual.Annotations[0].VertexId;
        var values = actual.Annotations.ToDictionary(static item => item.VertexId, static item => item.Value);
        for (int i = 0; i < expected.Length; i++)
        {
            VertexId id = WithValue(source, i);
            if (include(expected[i])) values[id].Should().Be(expected[i]);
            else values.Should().NotContainKey(id);
        }
    }

    private static GraphCase CreateGraph(int vertexCount, Arc[] arcs)
    {
        QuiverDatabase database = QuiverDatabase.CreateInMemory();
        var weights = new Dictionary<EdgeId, double>();
        VertexId[] vertices;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            vertices = Enumerable.Range(0, vertexCount).Select(_ => write.CreateVertex("Node")).ToArray();
            foreach (Arc arc in arcs)
            {
                EdgeId edge = write.CreateEdge(vertices[arc.Source], vertices[arc.Target], "Arc");
                weights.Add(edge, arc.Value);
            }
            write.Commit();
        }
        return new(database, database.BeginReadTransaction(), vertices, arcs, weights);
    }

    private static Arc[] CreateDag(int vertexCount, int seed, bool allowNegative)
    {
        var random = new Random(seed);
        var arcs = new List<Arc>();
        for (int vertex = 0; vertex < vertexCount - 1; vertex++)
            arcs.Add(new(vertex, vertex + 1, Weight(random, allowNegative)));
        for (int source = 0; source < vertexCount; source++)
            for (int target = source + 2; target < vertexCount; target++)
                if (random.NextDouble() < 0.25) arcs.Add(new(source, target, Weight(random, allowNegative)));
        return arcs.ToArray();
    }

    private static double Weight(Random random, bool allowNegative)
        => allowNegative ? random.Next(-20, 51) / 10.0 : 0.1 + random.NextDouble() * 5;

    private static bool[] OracleBfs(int vertexCount, Arc[] arcs)
    {
        List<int>[] outgoing = Outgoing(vertexCount, arcs);
        var result = new bool[vertexCount];
        var queue = new Queue<int>();
        result[0] = true;
        queue.Enqueue(0);
        while (queue.TryDequeue(out int source))
            foreach (int target in outgoing[source])
                if (!result[target]) { result[target] = true; queue.Enqueue(target); }
        return result;
    }

    private static double[] OracleDijkstra(int vertexCount, Arc[] arcs)
    {
        var distance = Enumerable.Repeat(double.PositiveInfinity, vertexCount).ToArray();
        var settled = new bool[vertexCount];
        distance[0] = 0;
        for (int step = 0; step < vertexCount; step++)
        {
            int best = -1;
            for (int vertex = 0; vertex < vertexCount; vertex++)
                if (!settled[vertex] && (best < 0 || distance[vertex] < distance[best])) best = vertex;
            if (best < 0 || double.IsPositiveInfinity(distance[best])) break;
            settled[best] = true;
            foreach (Arc edge in arcs.Where(edge => edge.Source == best))
                distance[edge.Target] = Math.Min(distance[edge.Target], distance[best] + edge.Value);
        }
        return distance;
    }

    private static double[] OracleDagTropical(int vertexCount, Arc[] arcs)
    {
        var distance = Enumerable.Repeat(double.PositiveInfinity, vertexCount).ToArray();
        distance[0] = 0;
        for (int source = 0; source < vertexCount; source++)
            foreach (Arc edge in arcs.Where(edge => edge.Source == source))
                distance[edge.Target] = Math.Min(distance[edge.Target], distance[source] + edge.Value);
        return distance;
    }

    private static double[] OracleViterbi(int vertexCount, Arc[] arcs)
    {
        var probability = new double[vertexCount];
        probability[0] = 1;
        for (int source = 0; source < vertexCount; source++)
            foreach (Arc edge in arcs.Where(edge => edge.Source == source))
                probability[edge.Target] = Math.Max(probability[edge.Target], probability[source] * edge.Value);
        return probability;
    }

    private static double[] OracleBellmanFord(int vertexCount, Arc[] arcs)
    {
        var distance = Enumerable.Repeat(double.PositiveInfinity, vertexCount).ToArray();
        distance[0] = 0;
        for (int pass = 1; pass < vertexCount; pass++)
        {
            bool changed = false;
            foreach (Arc edge in arcs)
            {
                double candidate = distance[edge.Source] + edge.Value;
                if (candidate >= distance[edge.Target]) continue;
                distance[edge.Target] = candidate;
                changed = true;
            }
            if (!changed) break;
        }
        return distance;
    }

    private static List<int>[] Outgoing(int vertexCount, Arc[] arcs)
    {
        var outgoing = Enumerable.Range(0, vertexCount).Select(_ => new List<int>()).ToArray();
        foreach (Arc edge in arcs) outgoing[edge.Source].Add(edge.Target);
        return outgoing;
    }

    private static GraphAnnotationOptions WithOptions(
        GraphAnnotationOptions source,
        string? edgeType = null,
        int? maxVertices = null,
        int? maxEdges = null,
        int? maxResults = null,
        long? maxRelaxations = null,
        long? maxAnnotationUpdates = null,
        TimeSpan? timeLimit = null,
        CancellationToken? cancellationToken = null)
        => new()
        {
            EdgeType = edgeType ?? source.EdgeType,
            MaxVertices = maxVertices ?? source.MaxVertices,
            MaxEdges = maxEdges ?? source.MaxEdges,
            MaxResults = maxResults ?? source.MaxResults,
            MaxRelaxations = maxRelaxations ?? source.MaxRelaxations,
            MaxAnnotationUpdates = maxAnnotationUpdates ?? source.MaxAnnotationUpdates,
            TimeLimit = timeLimit ?? source.TimeLimit,
            CancellationToken = cancellationToken ?? source.CancellationToken,
        };

    private static VertexId WithValue(VertexId source, int sequence) => VertexId.Create(sequence, source.Generation);

    private readonly record struct Arc(int Source, int Target, double Value);

    private sealed class GraphCase(
        QuiverDatabase database,
        IReadTransaction read,
        VertexId[] vertices,
        Arc[] arcs,
        Dictionary<EdgeId, double> weights) : IDisposable
    {
        internal IReadTransaction Read { get; } = read;
        internal VertexId[] Vertices { get; } = vertices;
        internal Arc[] Arcs { get; } = arcs;
        internal double SelectValue(IReadTransaction _, EdgeId edge) => weights[edge];
        public void Dispose() { Read.Dispose(); database.Dispose(); }
    }

    private sealed class TestProviderException : Exception;
}
