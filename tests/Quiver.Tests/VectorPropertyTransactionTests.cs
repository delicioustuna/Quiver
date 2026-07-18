using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class VectorPropertyTransactionTests : IDisposable
{
    private const string IndexName = "idx_embedding";
    private const string PropertyName = "embedding";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "quiver_vector_property_" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "graph.quiver");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Vector_property_round_trips_without_an_index()
    {
        VertexId vertex;
        using (var database = QuiverDatabase.Open(DatabasePath))
        {
            using var write = database.BeginWriteTransaction();
            vertex = write.CreateVertex("Document");
            write.SetVectorProperty(
                EntityRef.From(vertex),
                PropertyName,
                [1f, 2f, 3f]);
            write.Commit();
        }

        using var reopened = QuiverDatabase.Open(DatabasePath);
        using var read = reopened.BeginReadTransaction();
        var destination = new float[3];
        read.TryGetVectorProperty(EntityRef.From(vertex), PropertyName, destination)
            .Should().BeTrue();
        destination.Should().Equal(1f, 2f, 3f);
    }

    [Fact]
    public void Vector_definition_round_trips_target_and_segment_policy()
    {
        var expected = new VectorIndexDefinition(
            IndexName,
            new PropertyTarget(
                PropertyOwnerKind.Vertex,
                PropertyName,
                "Document"),
            Dimensions: 3,
            Metric: DistanceMetric.Dot,
            HnswM: 16,
            HnswMMax0: 32,
            HnswMaxLayers: 6,
            HnswEfConstruction: 120,
            SegmentPolicy: new VectorSegmentPolicy(
                MaximumDeltaEntries: 17,
                MaximumSegments: 5));
        using (var database = QuiverDatabase.Open(DatabasePath))
        {
            using var write = database.BeginWriteTransaction();
            write.EditSchema.CreateIndex(expected);
            write.Commit();
        }

        using var reopened = QuiverDatabase.Open(DatabasePath);
        using var read = reopened.BeginReadTransaction();
        read.Schema.TryGetIndex(IndexName, out IndexInfo info).Should().BeTrue();
        info.Definition.Should().Be(expected);
    }

    [Fact]
    public void Rolled_back_vector_definition_is_not_visible_after_reopen()
    {
        using (var database = QuiverDatabase.Open(DatabasePath))
        {
            using var write = database.BeginWriteTransaction();
            write.EditSchema.CreateIndex(new VectorIndexDefinition(
                IndexName,
                new PropertyTarget(
                    PropertyOwnerKind.Vertex,
                    PropertyName,
                    "Document"),
                Dimensions: 3));
            write.Rollback();
        }

        using var reopened = QuiverDatabase.Open(DatabasePath);
        using var read = reopened.BeginReadTransaction();
        read.Schema.TryGetIndex(IndexName, out _).Should().BeFalse();
    }

    [Fact]
    public void Vector_property_and_index_delta_roll_back_together()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        CreateIndex(database);
        using (var write = database.BeginWriteTransaction())
        {
            VertexId vertex = write.CreateVertex("Document");
            write.SetVectorProperty(EntityRef.From(vertex), PropertyName, [1f, 0f, 0f]);
            write.Rollback();
        }

        using var read = database.BeginReadTransaction();
        using VectorSearchCursor results = read.KnnSearch(IndexName, [1f, 0f, 0f], 10);
        results.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Dropping_an_index_does_not_remove_the_vector_property()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        CreateIndex(database);
        VertexId vertex;
        using (var write = database.BeginWriteTransaction())
        {
            vertex = write.CreateVertex("Document");
            write.SetVectorProperty(EntityRef.From(vertex), PropertyName, [0f, 1f, 0f]);
            write.Commit();
        }
        using (var schema = database.BeginWriteTransaction())
        {
            schema.EditSchema.DropIndex(IndexName);
            schema.Commit();
        }

        using var read = database.BeginReadTransaction();
        var destination = new float[3];
        read.TryGetVectorProperty(EntityRef.From(vertex), PropertyName, destination)
            .Should().BeTrue();
        destination.Should().Equal(0f, 1f, 0f);
    }

    [Fact]
    public void Knn_returns_full_owner_identity_and_revalidates_removed_properties()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        CreateIndex(database);
        VertexId vertex;
        using (var write = database.BeginWriteTransaction())
        {
            vertex = write.CreateVertex("Document");
            write.SetVectorProperty(EntityRef.From(vertex), PropertyName, [1f, 0f, 0f]);
            write.Commit();
        }

        using (var read = database.BeginReadTransaction())
        using (VectorSearchCursor results = read.KnnSearch(IndexName, [1f, 0f, 0f], 10))
        {
            results.MoveNext().Should().BeTrue();
            results.Current.Owner.Should().Be(EntityRef.From(vertex));
        }

        using (var write = database.BeginWriteTransaction())
        {
            write.RemoveProperty(vertex, PropertyName);
            write.Commit();
        }
        using var afterDelete = database.BeginReadTransaction();
        using VectorSearchCursor empty = afterDelete.KnnSearch(IndexName, [1f, 0f, 0f], 10);
        empty.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Reused_sequence_does_not_alias_the_deleted_owner_vector()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        CreateIndex(database);
        VertexId deleted;
        using (var write = database.BeginWriteTransaction())
        {
            deleted = write.CreateVertex("Document");
            write.SetVectorProperty(
                EntityRef.From(deleted),
                PropertyName,
                [1f, 0f, 0f]);
            write.Commit();
        }
        using (var write = database.BeginWriteTransaction())
        {
            write.DeleteVertex(deleted);
            write.Commit();
        }
        database.Vacuum();

        VertexId replacement;
        using (var write = database.BeginWriteTransaction())
        {
            replacement = write.CreateVertex("Document");
            write.Commit();
        }
        replacement.Sequence.Should().Be(deleted.Sequence);
        replacement.Generation.Should().NotBe(deleted.Generation);

        using var read = database.BeginReadTransaction();
        using VectorSearchCursor results =
            read.KnnSearch(IndexName, [1f, 0f, 0f], 10);
        results.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Batch_search_matches_individual_queries_on_one_snapshot()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        CreateIndex(database);
        using (var write = database.BeginWriteTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                VertexId owner = write.CreateVertex("Document");
                var vector = new float[3];
                vector[i] = 1f;
                write.SetVectorProperty(
                    EntityRef.From(owner),
                    PropertyName,
                    vector);
            }
            write.Commit();
        }

        ReadOnlyMemory<float>[] queries =
        [
            new float[] { 1f, 0f, 0f },
            new float[] { 0f, 1f, 0f },
        ];
        using var read = database.BeginReadTransaction();
        IReadOnlyList<VectorSearchCursor> batch =
            read.KnnSearchBatch(IndexName, queries, 2);
        try
        {
            for (int i = 0; i < queries.Length; i++)
            {
                using VectorSearchCursor single =
                    read.KnnSearch(IndexName, queries[i].Span, 2);
                ReadOwners(batch[i]).Should().Equal(ReadOwners(single));
            }
        }
        finally
        {
            foreach (VectorSearchCursor cursor in batch)
                cursor.Dispose();
        }
    }

    [Fact]
    public void All_transaction_and_traversal_routes_validate_search_options()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        CreateIndex(database);
        using (var write = database.BeginWriteTransaction())
        {
            VertexId owner = write.CreateVertex("Document");
            write.SetVectorProperty(
                EntityRef.From(owner),
                PropertyName,
                [1f, 0f, 0f]);
            write.Commit();
        }

        var invalid = new VectorSearchOptions
        {
            FilteredOversampleFactor = 0,
        };
        using var read = database.BeginReadTransaction();
        Action single = () => read.KnnSearch(
            IndexName,
            [1f, 0f, 0f],
            1,
            invalid);
        Action batch = () => read.KnnSearchBatch(
            IndexName,
            new ReadOnlyMemory<float>[] { new float[] { 1f, 0f, 0f } },
            1,
            invalid);
        Action traversal = () => read.Query
            .Knn(IndexName, [1f, 0f, 0f], 1, invalid)
            .ToList();
        Action filtered = () => read.Query
            .Vertices()
            .FilterByKnn(IndexName, [1f, 0f, 0f], 1, invalid)
            .ToList();

        single.Should().Throw<VectorException>()
            .WithMessage("*FilteredOversampleFactor*");
        batch.Should().Throw<VectorException>()
            .WithMessage("*FilteredOversampleFactor*");
        traversal.Should().Throw<VectorException>()
            .WithMessage("*FilteredOversampleFactor*");
        filtered.Should().Throw<VectorException>()
            .WithMessage("*FilteredOversampleFactor*");
    }

    [Fact]
    public void Filtered_knn_keeps_full_typed_candidates()
    {
        using var database = QuiverDatabase.Open(DatabasePath);
        CreateIndex(database);
        VertexId expected;
        using (var write = database.BeginWriteTransaction())
        {
            expected = write.CreateVertex("Document");
            VertexId excluded = write.CreateVertex("Document");
            PropertyValue keep = PropertyValue.FromString("keep");
            PropertyValue drop = PropertyValue.FromString("drop");
            write.SetProperty(expected, "group", in keep);
            write.SetProperty(excluded, "group", in drop);
            write.SetVectorProperty(
                EntityRef.From(expected),
                PropertyName,
                [0.8f, 0.2f, 0f]);
            write.SetVectorProperty(
                EntityRef.From(excluded),
                PropertyName,
                [1f, 0f, 0f]);
            write.Commit();
        }

        using var read = database.BeginReadTransaction();
        List<VertexId> results = read.Query
            .Vertices()
            .Has("group", "keep")
            .FilterByKnn(IndexName, [1f, 0f, 0f], 10)
            .ToList();
        results.Should().ContainSingle().Which.Should().Be(expected);
    }

    private static List<EntityRef> ReadOwners(VectorSearchCursor cursor)
    {
        var owners = new List<EntityRef>();
        while (cursor.MoveNext())
            owners.Add(cursor.Current.Owner);
        return owners;
    }

    private static void CreateIndex(QuiverDatabase database)
    {
        using var write = database.BeginWriteTransaction();
        write.EditSchema.CreateIndex(new VectorIndexDefinition(
            IndexName,
            new PropertyTarget(PropertyOwnerKind.Vertex, PropertyName, "Document"),
            Dimensions: 3,
            Metric: DistanceMetric.Cosine));
        write.Commit();
    }
}
