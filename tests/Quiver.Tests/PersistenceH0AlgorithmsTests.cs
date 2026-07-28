using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class PersistenceH0AlgorithmsTests
{
    [Fact]
    public void Complete_filtration_matches_independent_small_mst_oracle()
    {
        float[][] vectors = CreatePoints(12, 5, 0x50424830);
        using TestDatabase data = CreateDatabase(vectors);

        PersistenceH0Result result = PersistenceH0Algorithms.Compute(
            data.Read,
            TestDatabase.IndexName,
            CompleteOptions());

        float[] expected = ComputeOracleDeaths(vectors);
        result.IsComplete.Should().BeTrue();
        result.IsExact.Should().BeTrue();
        result.Kind.Should().Be(PersistenceH0ResultKind.Exact);
        result.PointCount.Should().Be(vectors.Length);
        result.ComponentCount.Should().Be(1);
        result.Intervals.Where(interval => interval.Death.HasValue)
            .Select(interval => interval.Death!.Value)
            .Should().Equal(expected, (actual, oracle) => Math.Abs(actual - oracle) < 1e-5f);
        result.Intervals.Should().ContainSingle(interval => !interval.Death.HasValue && !interval.IsRightCensored);
    }

    [Fact]
    public void Sparse_knn_reports_approximation_and_analyzes_complete_index_population()
    {
        float[][] vectors =
        [
            [0, 0], [0, 1],
            [100, 0], [100, 1],
            [200, 0], [200, 1],
        ];
        using TestDatabase data = CreateDatabase(vectors);

        PersistenceH0Result result = PersistenceH0Algorithms.Compute(
            data.Read,
            TestDatabase.IndexName,
            new PersistenceH0Options
            {
                Filtration = PersistenceH0Filtration.SparseKnn,
                NeighborCount = 1,
                MaxDistanceEvaluations = 100,
            });

        result.PointCount.Should().Be(vectors.Length);
        result.IsComplete.Should().BeTrue();
        result.IsExact.Should().BeFalse();
        result.Kind.Should().Be(PersistenceH0ResultKind.SparseApproximation);
        result.ComponentCount.Should().Be(3);
        result.Intervals.Should().HaveCount(vectors.Length);
    }

    [Fact]
    public void Complete_filtration_supports_edge_and_nexus_vector_index_targets()
    {
        using var database = QuiverDatabase.CreateInMemory();
        using (IWriteTransaction schema = database.BeginWriteTransaction())
        {
            schema.EditSchema.CreateIndex(new VectorIndexDefinition(
                "edge_positions",
                new PropertyTarget(PropertyOwnerKind.Edge, "position", "Link"),
                2,
                DistanceMetric.Euclidean));
            schema.EditSchema.CreateIndex(new VectorIndexDefinition(
                "nexus_positions",
                new PropertyTarget(PropertyOwnerKind.Nexus, "position", "Group"),
                2,
                DistanceMetric.Euclidean));
            schema.Commit();
        }
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            VertexId first = write.CreateVertex("Item");
            VertexId second = write.CreateVertex("Item");
            EdgeId edge0 = write.CreateEdge(first, second, "Link");
            EdgeId edge1 = write.CreateEdge(second, first, "Link");
            write.SetVectorProperty(EntityRef.From(edge0), "position", [0, 0]);
            write.SetVectorProperty(EntityRef.From(edge1), "position", [1, 0]);
            NexusId nexus0 = write.CreateNexus("Group", [new("member", first), new("member", second)]);
            NexusId nexus1 = write.CreateNexus("Group", [new("left", first), new("right", second)]);
            write.SetVectorProperty(EntityRef.From(nexus0), "position", [0, 0]);
            write.SetVectorProperty(EntityRef.From(nexus1), "position", [2, 0]);
            write.Commit();
        }
        using IReadTransaction read = database.BeginReadTransaction();

        PersistenceH0Result edges = PersistenceH0Algorithms.Compute(read, "edge_positions", CompleteOptions());
        PersistenceH0Result nexuses = PersistenceH0Algorithms.Compute(read, "nexus_positions", CompleteOptions());

        edges.PointCount.Should().Be(2);
        FiniteDeaths(edges).Should().Equal(1);
        nexuses.PointCount.Should().Be(2);
        FiniteDeaths(nexuses).Should().Equal(2);
    }

    [Fact]
    public void Snapshot_population_does_not_mix_updates_or_deleted_owners()
    {
        using var database = QuiverDatabase.CreateInMemory();
        CreateSchema(database, 2);
        VertexId changed;
        VertexId removed;
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            changed = AddPoint(write, [0, 0]);
            removed = AddPoint(write, [1, 0]);
            AddPoint(write, [2, 0]);
            write.Commit();
        }
        using IReadTransaction oldRead = database.BeginReadTransaction();
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            write.SetVectorProperty(EntityRef.From(changed), TestDatabase.PropertyName, [100, 0]);
            write.DeleteVertex(removed);
            AddPoint(write, [101, 0]);
            write.Commit();
        }
        using IReadTransaction newRead = database.BeginReadTransaction();

        PersistenceH0Result oldResult = PersistenceH0Algorithms.Compute(oldRead, TestDatabase.IndexName, CompleteOptions());
        PersistenceH0Result newResult = PersistenceH0Algorithms.Compute(newRead, TestDatabase.IndexName, CompleteOptions());

        oldResult.PointCount.Should().Be(3);
        newResult.PointCount.Should().Be(3);
        FiniteDeaths(oldResult).Should().Equal(1, 1);
        FiniteDeaths(newResult).Should().Equal(1, 98);
    }

    [Theory]
    [InlineData(DistanceMetric.Cosine)]
    [InlineData(DistanceMetric.Dot)]
    public void Non_euclidean_index_is_rejected_instead_of_relabeling_score_as_distance(DistanceMetric metric)
    {
        using var database = QuiverDatabase.CreateInMemory();
        CreateSchema(database, 2, metric);
        using (IWriteTransaction write = database.BeginWriteTransaction())
        {
            AddPoint(write, [1, 0]);
            write.Commit();
        }
        using IReadTransaction read = database.BeginReadTransaction();

        Action act = () => PersistenceH0Algorithms.Compute(read, TestDatabase.IndexName, CompleteOptions());

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Non_finite_coordinates_are_rejected_instead_of_producing_an_exact_label()
    {
        using TestDatabase data = CreateDatabase([[0, 0], [float.NaN, 1]]);

        Action act = () => PersistenceH0Algorithms.Compute(
            data.Read, TestDatabase.IndexName, CompleteOptions());

        act.Should().Throw<VectorException>();
    }

    [Theory]
    [InlineData(PersistenceH0Filtration.Complete)]
    [InlineData(PersistenceH0Filtration.SparseKnn)]
    public void Overflowed_euclidean_distance_is_rejected(
        PersistenceH0Filtration filtration)
    {
        using TestDatabase data = CreateDatabase(
            [[float.MaxValue, 0], [-float.MaxValue, 0]]);
        PersistenceH0Options options = CompleteOptions() with
        {
            Filtration = filtration,
            NeighborCount = 1,
        };

        Action act = () => PersistenceH0Algorithms.Compute(
            data.Read, TestDatabase.IndexName, options);

        act.Should().Throw<VectorException>();
    }

    [Fact]
    public void Empty_and_singleton_populations_have_explicit_boundary_barcodes()
    {
        using var emptyDatabase = QuiverDatabase.CreateInMemory();
        CreateSchema(emptyDatabase, 2);
        using IReadTransaction emptyRead = emptyDatabase.BeginReadTransaction();
        using TestDatabase singletonData = CreateDatabase([[4, 2]]);

        foreach (PersistenceH0Filtration filtration in Enum.GetValues<PersistenceH0Filtration>())
        {
            PersistenceH0Options options = CompleteOptions() with { Filtration = filtration };
            PersistenceH0Result empty = PersistenceH0Algorithms.Compute(
                emptyRead, TestDatabase.IndexName, options);
            PersistenceH0Result singleton = PersistenceH0Algorithms.Compute(
                singletonData.Read, TestDatabase.IndexName, options);

            empty.IsComplete.Should().BeTrue();
            empty.IsExact.Should().Be(filtration == PersistenceH0Filtration.Complete);
            empty.Kind.Should().Be(filtration == PersistenceH0Filtration.Complete
                ? PersistenceH0ResultKind.Exact
                : PersistenceH0ResultKind.SparseApproximation);
            empty.PointCount.Should().Be(0);
            empty.TotalIntervalCount.Should().Be(0);
            empty.ComponentCount.Should().Be(0);
            empty.DistanceEvaluations.Should().Be(0);
            empty.EdgeCount.Should().Be(0);
            empty.Intervals.Should().BeEmpty();

            singleton.IsComplete.Should().BeTrue();
            singleton.IsExact.Should().Be(filtration == PersistenceH0Filtration.Complete);
            singleton.PointCount.Should().Be(1);
            singleton.TotalIntervalCount.Should().Be(1);
            singleton.ComponentCount.Should().Be(1);
            singleton.DistanceEvaluations.Should().Be(
                filtration == PersistenceH0Filtration.Complete ? 0 : 1);
            singleton.EdgeCount.Should().Be(0);
            singleton.Intervals.Should().ContainSingle()
                .Which.Should().Be(new PersistenceH0Interval(0, null, false));
        }
    }

    [Fact]
    public void Work_and_result_limits_return_non_misleading_incomplete_contracts()
    {
        using TestDatabase data = CreateDatabase(CreatePoints(6, 2, 42));

        PersistenceH0Result points = PersistenceH0Algorithms.Compute(data.Read, TestDatabase.IndexName,
            CompleteOptions() with { MaxPoints = 5 });
        PersistenceH0Result edges = PersistenceH0Algorithms.Compute(data.Read, TestDatabase.IndexName,
            CompleteOptions() with { MaxEdges = 14 });
        PersistenceH0Result distances = PersistenceH0Algorithms.Compute(data.Read, TestDatabase.IndexName,
            CompleteOptions() with { MaxDistanceEvaluations = 14 });
        PersistenceH0Result results = PersistenceH0Algorithms.Compute(data.Read, TestDatabase.IndexName,
            CompleteOptions() with { MaxResults = 3 });

        AssertNoBarcode(points, PersistenceH0TerminationReason.MaxPointsReached);
        points.PointCount.Should().BeNull();
        points.TotalIntervalCount.Should().BeNull();
        points.ComponentCount.Should().BeNull();
        AssertNoBarcode(edges, PersistenceH0TerminationReason.MaxEdgesReached);
        edges.PointCount.Should().Be(6);
        edges.ComponentCount.Should().BeNull();
        AssertNoBarcode(distances, PersistenceH0TerminationReason.MaxDistanceEvaluationsReached);
        results.Kind.Should().Be(PersistenceH0ResultKind.Incomplete);
        results.IsExact.Should().BeFalse();
        results.TerminationReason.Should().Be(PersistenceH0TerminationReason.MaxResultsReached);
        results.Intervals.Should().HaveCount(3);
        results.TotalIntervalCount.Should().Be(6);
    }

    [Fact]
    public void Sparse_edge_cap_is_not_exceeded_while_building_the_graph()
    {
        using TestDatabase data = CreateDatabase(CreatePoints(8, 2, 55));

        PersistenceH0Result result = PersistenceH0Algorithms.Compute(data.Read, TestDatabase.IndexName,
            new PersistenceH0Options
            {
                Filtration = PersistenceH0Filtration.SparseKnn,
                NeighborCount = 4,
                MaxEdges = 3,
                MaxDistanceEvaluations = 100,
                TimeLimit = Timeout.InfiniteTimeSpan,
            });

        AssertNoBarcode(result, PersistenceH0TerminationReason.MaxEdgesReached);
        result.EdgeCount.Should().Be(3);
    }

    [Fact]
    public void Cancellation_and_time_budget_return_no_partial_barcode()
    {
        using TestDatabase data = CreateDatabase(CreatePoints(32, 4, 91));
        using var source = new CancellationTokenSource();
        source.Cancel();

        PersistenceH0Result cancelled = PersistenceH0Algorithms.Compute(data.Read, TestDatabase.IndexName,
            CompleteOptions() with { CancellationToken = source.Token });
        PersistenceH0Result timed = PersistenceH0Algorithms.Compute(data.Read, TestDatabase.IndexName,
            CompleteOptions() with { TimeLimit = TimeSpan.Zero });

        AssertNoBarcode(cancelled, PersistenceH0TerminationReason.Cancelled);
        AssertNoBarcode(timed, PersistenceH0TerminationReason.TimeBudget);
    }

    [Fact]
    public void Scale_limit_marks_surviving_intervals_as_right_censored()
    {
        using TestDatabase data = CreateDatabase([[0, 0], [1, 0], [10, 0]]);

        PersistenceH0Result result = PersistenceH0Algorithms.Compute(data.Read, TestDatabase.IndexName,
            CompleteOptions() with { MaxScale = 2 });

        result.IsExact.Should().BeTrue();
        FiniteDeaths(result).Should().Equal(1);
        result.ComponentCount.Should().Be(2);
        result.Intervals.Count(interval => interval.IsRightCensored).Should().Be(2);
        Action estimate = () => PersistenceH0Algorithms.EstimateClusters(result);
        estimate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cluster_estimate_is_a_separate_heuristic_result()
    {
        using TestDatabase data = CreateDatabase(
        [
            [0, 0], [0, 1], [1, 0],
            [20, 0], [20, 1], [21, 0],
            [40, 0], [40, 1], [41, 0],
        ]);
        PersistenceH0Result barcode = PersistenceH0Algorithms.Compute(
            data.Read, TestDatabase.IndexName, CompleteOptions());

        PersistenceClusterEstimate estimate = PersistenceH0Algorithms.EstimateClusters(barcode);

        barcode.Should().BeOfType<PersistenceH0Result>();
        estimate.Should().BeOfType<PersistenceClusterEstimate>();
        estimate.ClusterCount.Should().Be(3);
        estimate.UsedDisconnectedComponents.Should().BeFalse();
        estimate.SeparationScale.Should().NotBeNull();
    }

    [Fact]
    public void Gaussian_mixture_cluster_estimate_recovers_fixed_seed_structures()
    {
        for (int seed = 0; seed < 5; seed++)
        {
            float[][] vectors = CreateGaussianMixture(100, 8, seed + 700);
            using TestDatabase data = CreateDatabase(vectors);
            PersistenceH0Result barcode = PersistenceH0Algorithms.Compute(
                data.Read, TestDatabase.IndexName, CompleteOptions());

            PersistenceClusterEstimate estimate = PersistenceH0Algorithms.EstimateClusters(barcode);

            estimate.ClusterCount.Should().Be(5, $"seed {seed} has five planted groups");
        }
    }

    [Fact]
    public void Isolated_outlier_produces_a_clearly_long_lived_component()
    {
        using TestDatabase data = CreateDatabase([[0, 0], [0, 1], [1, 0], [100, 0]]);

        PersistenceH0Result barcode = PersistenceH0Algorithms.Compute(
            data.Read, TestDatabase.IndexName, CompleteOptions());
        float[] deaths = FiniteDeaths(barcode);

        deaths.Should().HaveCount(3);
        deaths[^1].Should().BeGreaterThan(50);
        deaths[^1].Should().BeGreaterThan(deaths[^2] * 50);
    }

    private static PersistenceH0Options CompleteOptions() => new()
    {
        Filtration = PersistenceH0Filtration.Complete,
        MaxPoints = 1_000,
        MaxEdges = 1_000_000,
        MaxDistanceEvaluations = 1_000_000,
        MaxResults = 1_000,
        TimeLimit = Timeout.InfiniteTimeSpan,
    };

    private static float[] FiniteDeaths(PersistenceH0Result result) => result.Intervals
        .Where(interval => interval.Death.HasValue)
        .Select(interval => interval.Death!.Value)
        .ToArray();

    private static void AssertNoBarcode(
        PersistenceH0Result result,
        PersistenceH0TerminationReason reason)
    {
        result.IsComplete.Should().BeFalse();
        result.IsExact.Should().BeFalse();
        result.Kind.Should().Be(PersistenceH0ResultKind.Incomplete);
        result.TerminationReason.Should().Be(reason);
        result.Intervals.Should().BeEmpty();
    }

    private static float[] ComputeOracleDeaths(IReadOnlyList<float[]> vectors)
    {
        var edges = new List<(int Left, int Right, float Distance)>();
        for (int left = 0; left < vectors.Count; left++)
        {
            for (int right = left + 1; right < vectors.Count; right++)
                edges.Add((left, right, Distance(vectors[left], vectors[right])));
        }
        edges.Sort((left, right) => left.Distance != right.Distance
            ? left.Distance.CompareTo(right.Distance)
            : left.Left != right.Left
                ? left.Left.CompareTo(right.Left)
                : left.Right.CompareTo(right.Right));
        int[] component = Enumerable.Range(0, vectors.Count).ToArray();
        var deaths = new List<float>();
        foreach ((int left, int right, float distance) in edges)
        {
            int leftComponent = component[left];
            int rightComponent = component[right];
            if (leftComponent == rightComponent)
                continue;
            deaths.Add(distance);
            for (int i = 0; i < component.Length; i++)
            {
                if (component[i] == rightComponent)
                    component[i] = leftComponent;
            }
        }
        return deaths.ToArray();
    }

    private static float Distance(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        float sum = 0;
        for (int i = 0; i < left.Count; i++)
        {
            float difference = left[i] - right[i];
            sum += difference * difference;
        }
        return MathF.Sqrt(sum);
    }

    private static float[][] CreatePoints(int count, int dimensions, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Enumerable.Range(0, dimensions)
                .Select(_ => (float)random.NextDouble())
                .ToArray())
            .ToArray();
    }

    private static float[][] CreateGaussianMixture(int count, int dimensions, int seed)
    {
        var random = new Random(seed);
        var vectors = new float[count][];
        for (int point = 0; point < count; point++)
        {
            int cluster = point % 5;
            vectors[point] = new float[dimensions];
            vectors[point][0] = cluster * 20;
            vectors[point][1] = cluster % 2 * 8;
            for (int dimension = 0; dimension < dimensions; dimension++)
                vectors[point][dimension] += (float)(0.15 * NextGaussian(random));
        }
        return vectors;
    }

    private static double NextGaussian(Random random)
    {
        double first = 1 - random.NextDouble();
        double second = 1 - random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(first)) * Math.Cos(2 * Math.PI * second);
    }

    private static TestDatabase CreateDatabase(float[][] vectors)
    {
        var database = new TestDatabase();
        CreateSchema(database.Database, vectors[0].Length);
        using (IWriteTransaction write = database.Database.BeginWriteTransaction())
        {
            foreach (float[] vector in vectors)
                AddPoint(write, vector);
            write.Commit();
        }
        database.OpenRead();
        return database;
    }

    private static VertexId AddPoint(IWriteTransaction write, ReadOnlySpan<float> vector)
    {
        VertexId point = write.CreateVertex("Point");
        write.SetVectorProperty(EntityRef.From(point), TestDatabase.PropertyName, vector);
        return point;
    }

    private static void CreateSchema(
        QuiverDatabase database,
        int dimensions,
        DistanceMetric metric = DistanceMetric.Euclidean) =>
        database.EditSchema(schema => schema.CreateIndex(new VectorIndexDefinition(
            TestDatabase.IndexName,
            new PropertyTarget(PropertyOwnerKind.Vertex, TestDatabase.PropertyName, "Point"),
            dimensions,
            metric)));

    private sealed class TestDatabase : IDisposable
    {
        internal const string IndexName = "points";
        internal const string PropertyName = "position";

        internal QuiverDatabase Database { get; } = QuiverDatabase.CreateInMemory();
        internal IReadTransaction Read { get; private set; } = null!;

        internal void OpenRead() => Read = Database.BeginReadTransaction();

        public void Dispose()
        {
            Read?.Dispose();
            Database.Dispose();
        }
    }
}
