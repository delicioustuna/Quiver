using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Index;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

public sealed class Wave6ScalarIndexContractTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public Wave6ScalarIndexContractTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "quiver_wave6_scalar_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void CreateIndex_backfills_pre_existing_primary_properties()
    {
        using var db = QuiverDatabase.Open(_path);
        VertexId alice;
        using (var seed = db.BeginWriteTransaction())
        {
            alice = seed.CreateVertex("Person");
            seed.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
            seed.Commit();
        }

        using (var schema = db.BeginWriteTransaction())
        {
            schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_person_name",
                new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"),
                IndexKind.StringEquality));
            schema.Commit();
        }

        using var read = db.BeginReadTransaction();
        using var hits = read.SeekIndex(
            "idx_person_name",
            PropertyValue.FromString("Alice"));
        hits.MoveNext().Should().BeTrue();
        hits.Current.Should().Be(EntityRef.From(alice));
        hits.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Edge_and_nexus_targets_return_full_owner_identity()
    {
        using var db = QuiverDatabase.Open(_path);
        using (var schema = db.BeginWriteTransaction())
        {
            schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_edge_weight",
                new PropertyTarget(PropertyOwnerKind.Edge, "weight", "KNOWS"),
                IndexKind.Int64Equality));
            schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_fact_source",
                new PropertyTarget(PropertyOwnerKind.Nexus, "source", "Fact"),
                IndexKind.StringEquality));
            schema.Commit();
        }

        EdgeId edge;
        NexusId nexus;
        using (var write = db.BeginWriteTransaction())
        {
            var alice = write.CreateVertex("Person");
            var bob = write.CreateVertex("Person");
            edge = write.CreateEdge(alice, bob, "KNOWS");
            write.SetProperty(edge, "weight", PropertyValue.FromInt64(7));
            nexus = write.CreateNexus(
                "Fact",
                [
                    new NexusMember("subject", alice),
                    new NexusMember("object", bob),
                ]);
            write.SetProperty(nexus, "source", PropertyValue.FromString("manual"));
            write.Commit();
        }

        using var read = db.BeginReadTransaction();
        using var edgeHits = read.SeekIndex(
            "idx_edge_weight",
            PropertyValue.FromInt64(7));
        edgeHits.MoveNext().Should().BeTrue();
        edgeHits.Current.Should().Be(EntityRef.From(edge));
        edgeHits.MoveNext().Should().BeFalse();

        using var nexusHits = read.SeekIndex(
            "idx_fact_source",
            PropertyValue.FromString("manual"));
        nexusHits.MoveNext().Should().BeTrue();
        nexusHits.Current.Should().Be(EntityRef.From(nexus));
        nexusHits.MoveNext().Should().BeFalse();

        IndexConsistencyReport consistency =
            db.Diagnostics.CheckIndexConsistency();
        consistency.IndexCount.Should().Be(2);
        consistency.EntryCount.Should().Be(2);
        consistency.OrphanCount.Should().Be(0);
    }

    [Fact]
    public void Candidate_revalidation_rejects_wrong_scope_and_property_key()
    {
        using var db = QuiverDatabase.Open(_path);
        VertexId expected;
        using (var write = db.BeginWriteTransaction())
        {
            write.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_item_score",
                new PropertyTarget(
                    PropertyOwnerKind.Vertex,
                    "score",
                    "Item"),
                IndexKind.Int64Equality));

            expected = write.CreateVertex("Item");
            write.SetProperty(
                expected,
                "score",
                PropertyValue.FromInt64(10));

            VertexId wrongScope = write.CreateVertex("Other");
            write.SetProperty(
                wrongScope,
                "score",
                PropertyValue.FromInt64(10));
            VertexId wrongKey = write.CreateVertex("Item");
            write.SetProperty(
                wrongKey,
                "otherScore",
                PropertyValue.FromInt64(10));

            PropertyVersionRef wrongScopeVersion =
                SinglePropertyVersion(write, wrongScope);
            PropertyVersionRef wrongKeyVersion =
                SinglePropertyVersion(write, wrongKey);
            IBTreeIndex<long> index = write.AsInternal().Inner.Indexes
                .CreateInt64Index("idx_item_score");
            long key = 10;
            index.Insert(in key, wrongScopeVersion.Value);
            index.Insert(in key, wrongKeyVersion.Value);
            write.Commit();
        }

        using var read = db.BeginReadTransaction();
        using var hits = read.SeekIndex(
            "idx_item_score",
            PropertyValue.FromInt64(10));
        hits.MoveNext().Should().BeTrue();
        hits.Current.Should().Be(EntityRef.From(expected));
        hits.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Traversal_results_match_ready_index_and_primary_fallback()
    {
        using var db = QuiverDatabase.Open(_path);
        using (var write = db.BeginWriteTransaction())
        {
            write.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_item_score",
                new PropertyTarget(
                    PropertyOwnerKind.Vertex,
                    "score",
                    "Item"),
                IndexKind.Int64Equality));
            for (long score = 1; score <= 5; score++)
            {
                VertexId vertex = write.CreateVertex("Item");
                write.SetProperty(
                    vertex,
                    "score",
                    PropertyValue.FromInt64(score));
            }
            write.Commit();
        }

        List<VertexId> indexed;
        using (var read = db.BeginReadTransaction())
        {
            indexed = read.Query.Vertices()
                .HasLabel("Item")
                .Has("score", P.Between(2L, 4L))
                .ToList();
        }

        using (var mark = db.BeginWriteTransaction())
        {
            mark.AsInternal().Inner.Indexes.SetIndexState(
                "idx_item_score",
                IndexLifecycleState.RebuildRequired);
            mark.Commit();
        }

        using var fallback = db.BeginReadTransaction();
        fallback.Query.Vertices()
            .HasLabel("Item")
            .Has("score", P.Between(2L, 4L))
            .ToList()
            .Should().Equal(indexed);
    }

    [Fact]
    public void Old_and_new_readers_resolve_their_own_property_versions()
    {
        using var db = QuiverDatabase.Open(_path);
        VertexId vertex;
        using (var schema = db.BeginWriteTransaction())
        {
            schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_status",
                new PropertyTarget(PropertyOwnerKind.Vertex, "status", "Item"),
                IndexKind.StringEquality));
            schema.Commit();
        }
        using (var seed = db.BeginWriteTransaction())
        {
            vertex = seed.CreateVertex("Item");
            seed.SetProperty(vertex, "status", PropertyValue.FromString("old"));
            seed.Commit();
        }

        using var oldReader = db.BeginReadTransaction();
        using (var update = db.BeginWriteTransaction())
        {
            update.SetProperty(vertex, "status", PropertyValue.FromString("new"));
            update.Commit();
        }

        Seek(oldReader, "idx_status", "old").Should().Equal(EntityRef.From(vertex));
        Seek(oldReader, "idx_status", "new").Should().BeEmpty();

        using var newReader = db.BeginReadTransaction();
        Seek(newReader, "idx_status", "old").Should().BeEmpty();
        Seek(newReader, "idx_status", "new").Should().Equal(EntityRef.From(vertex));
    }

    [Fact]
    public void RebuildRequired_uses_primary_scan_then_writer_rebuilds_artifact()
    {
        using var db = QuiverDatabase.Open(_path);
        VertexId low;
        VertexId high;
        using (var schema = db.BeginWriteTransaction())
        {
            schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_score",
                new PropertyTarget(PropertyOwnerKind.Vertex, "score", "Item"),
                IndexKind.Int64Equality));
            schema.Commit();
        }
        using (var seed = db.BeginWriteTransaction())
        {
            high = seed.CreateVertex("Item");
            seed.SetProperty(high, "score", PropertyValue.FromInt64(20));
            low = seed.CreateVertex("Item");
            seed.SetProperty(low, "score", PropertyValue.FromInt64(10));
            seed.Commit();
        }
        using (var mark = db.BeginWriteTransaction())
        {
            mark.AsInternal().Inner.Indexes.SetIndexState(
                "idx_score",
                IndexLifecycleState.RebuildRequired);
            mark.Commit();
        }

        using (var fallback = db.BeginReadTransaction())
        {
            db.Schema.ListIndexes().Single(x => x.Name == "idx_score").State
                .Should().Be(IndexLifecycleState.RebuildRequired);
            Range(fallback, "idx_score", 0, 30)
                .Should().Equal(EntityRef.From(low), EntityRef.From(high));
        }

        using (var rebuild = db.BeginWriteTransaction())
            rebuild.Commit();

        bool rebuilt = SpinWait.SpinUntil(
                () => db.Schema.ListIndexes()
                    .Single(x => x.Name == "idx_score").State
                    == IndexLifecycleState.Ready,
                TimeSpan.FromSeconds(5));
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;
        rebuilt.Should().BeTrue(
            "the snapshot-built artifact should be published asynchronously; error: {0}",
            backend.ScalarIndexRebuildErrorForTest);

        using var indexed = db.BeginReadTransaction();
        db.Schema.ListIndexes().Single(x => x.Name == "idx_score").State
            .Should().Be(IndexLifecycleState.Ready);
        Range(indexed, "idx_score", 0, 30)
            .Should().Equal(EntityRef.From(low), EntityRef.From(high));
    }

    [Fact]
    public void Rebuild_retries_when_source_generation_changes_before_publish()
    {
        using var db = QuiverDatabase.Open(_path);
        VertexId vertex;
        using (var schema = db.BeginWriteTransaction())
        {
            schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_score",
                new PropertyTarget(PropertyOwnerKind.Vertex, "score", "Item"),
                IndexKind.Int64Equality));
            schema.Commit();
        }
        using (var seed = db.BeginWriteTransaction())
        {
            vertex = seed.CreateVertex("Item");
            seed.SetProperty(vertex, "score", PropertyValue.FromInt64(10));
            seed.Commit();
        }
        using (var mark = db.BeginWriteTransaction())
        {
            mark.AsInternal().Inner.Indexes.SetIndexState(
                "idx_score",
                IndexLifecycleState.RebuildRequired);
            mark.Commit();
        }

        BinaryGraphStorageBackend.ScalarIndexArtifactBuiltForTest = () =>
        {
            using var update = db.BeginWriteTransaction();
            update.SetProperty(vertex, "score", PropertyValue.FromInt64(20));
            update.Commit();
        };
        try
        {
            using (var trigger = db.BeginWriteTransaction())
                trigger.Commit();

            SpinWait.SpinUntil(
                    () => db.Schema.ListIndexes()
                        .Single(x => x.Name == "idx_score").State
                        == IndexLifecycleState.Ready,
                    TimeSpan.FromSeconds(5))
                .Should().BeTrue(
                    "a stale artifact should be discarded and rebuilt from the newer source generation");
        }
        finally
        {
            BinaryGraphStorageBackend.ScalarIndexArtifactBuiltForTest = null;
        }

        using var read = db.BeginReadTransaction();
        Range(read, "idx_score", 10, 10).Should().BeEmpty();
        Range(read, "idx_score", 20, 20).Should().Equal(EntityRef.From(vertex));
    }

    [Fact]
    public void Rebuild_waits_for_old_reader_horizon_and_preserves_both_snapshots()
    {
        using var db = QuiverDatabase.Open(_path);
        VertexId vertex;
        using (var schema = db.BeginWriteTransaction())
        {
            schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_score",
                new PropertyTarget(PropertyOwnerKind.Vertex, "score", "Item"),
                IndexKind.Int64Equality));
            schema.Commit();
        }
        using (var seed = db.BeginWriteTransaction())
        {
            vertex = seed.CreateVertex("Item");
            seed.SetProperty(vertex, "score", PropertyValue.FromInt64(10));
            seed.Commit();
        }

        var oldReader = db.BeginReadTransaction();
        try
        {
            using (var update = db.BeginWriteTransaction())
            {
                update.AsInternal().Inner.Indexes.SetIndexState(
                    "idx_score",
                    IndexLifecycleState.RebuildRequired);
                update.SetProperty(vertex, "score", PropertyValue.FromInt64(20));
                update.Commit();
            }
            using (var trigger = db.BeginWriteTransaction())
                trigger.Commit();

            Range(oldReader, "idx_score", 10, 10)
                .Should().Equal(EntityRef.From(vertex));
            Range(oldReader, "idx_score", 20, 20).Should().BeEmpty();

            using var newReader = db.BeginReadTransaction();
            newReader.Schema.ListIndexes()
                .Single(x => x.Name == "idx_score").State
                .Should().Be(IndexLifecycleState.RebuildRequired);
            Range(newReader, "idx_score", 10, 10).Should().BeEmpty();
            Range(newReader, "idx_score", 20, 20)
                .Should().Equal(EntityRef.From(vertex));
        }
        finally
        {
            oldReader.Dispose();
        }

        SpinWait.SpinUntil(
                () => db.Schema.ListIndexes()
                    .Single(x => x.Name == "idx_score").State
                    == IndexLifecycleState.Ready,
                TimeSpan.FromSeconds(5))
            .Should().BeTrue("publish should resume after the old reader leaves the horizon");

        using var indexed = db.BeginReadTransaction();
        Range(indexed, "idx_score", 20, 20)
            .Should().Equal(EntityRef.From(vertex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bulk_property_mutations_refresh_persisted_scalar_definitions(
        bool streaming)
    {
        using var db = QuiverDatabase.Open(_path);
        LabelId label;
        PropertyKeyId key;
        using (var schema = db.BeginWriteTransaction())
        {
            label = schema.EditSchema.GetOrCreateLabel("Document");
            key = schema.EditSchema.GetOrCreatePropertyKey("title");
            schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_document_title",
                new PropertyTarget(PropertyOwnerKind.Vertex, "title", "Document"),
                IndexKind.StringEquality));
            schema.Commit();
        }

        var vertex = VertexId.Create(0, 1);
        if (streaming)
        {
            using var bulk = db.BeginStreamingBulkLoad();
            bulk.AppendVertex(vertex, label);
            bulk.AppendProperty(vertex, key, PropertyValue.FromString("Wave 6"));
            bulk.Commit();
        }
        else
        {
            using var bulk = db.BeginBulkLoad();
            bulk.AppendVertex(vertex, label);
            bulk.AppendProperty(vertex, key, PropertyValue.FromString("Wave 6"));
            bulk.Commit();
        }

        using var read = db.BeginReadTransaction();
        Seek(read, "idx_document_title", "Wave 6")
            .Should().Equal(EntityRef.From(vertex));
        read.Schema.ListIndexes().Single(x => x.Name == "idx_document_title")
            .State.Should().Be(IndexLifecycleState.Ready);
    }

    [Fact]
    public void Int64_definition_routes_int32_values_to_the_same_physical_index()
    {
        using var db = QuiverDatabase.Open(_path);
        VertexId vertex;
        using (var write = db.BeginWriteTransaction())
        {
            write.EditSchema.CreateIndex(new ScalarIndexDefinition(
                "idx_rank",
                new PropertyTarget(PropertyOwnerKind.Vertex, "rank", "Item"),
                IndexKind.Int64Equality));
            vertex = write.CreateVertex("Item");
            write.SetProperty(vertex, "rank", PropertyValue.FromInt32(7));
            write.Commit();
        }

        using var read = db.BeginReadTransaction();
        using var hits = read.SeekIndex("idx_rank", PropertyValue.FromInt32(7));
        hits.MoveNext().Should().BeTrue();
        hits.Current.Should().Be(EntityRef.From(vertex));
        hits.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Missing_artifact_reopens_as_fallback_then_rebuilds()
    {
        VertexId vertex;
        using (var db = QuiverDatabase.Open(_path))
        {
            using (var write = db.BeginWriteTransaction())
            {
                write.EditSchema.CreateIndex(new ScalarIndexDefinition(
                    "idx_external_id",
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        "externalId",
                        "Document"),
                    IndexKind.StringEquality));
                vertex = write.CreateVertex("Document");
                write.SetProperty(
                    vertex,
                    "externalId",
                    PropertyValue.FromString("doc-1"));
                write.Commit();
            }

            using var damage = db.BeginWriteTransaction();
            var transactional = (TxIndexManager)damage.AsInternal().Inner.Indexes;
            ((IndexManager)transactional.Inner)
                .TruncateIndexArtifactForTest("idx_external_id");
            damage.Commit();
        }

        using var reopened = QuiverDatabase.Open(_path);
        using (var fallback = reopened.BeginReadTransaction())
        {
            fallback.Schema.ListIndexes()
                .Single(x => x.Name == "idx_external_id")
                .State.Should().Be(IndexLifecycleState.RebuildRequired);
            Seek(fallback, "idx_external_id", "doc-1")
                .Should().Equal(EntityRef.From(vertex));
        }

        using (var rebuild = reopened.BeginWriteTransaction())
            rebuild.Commit();

        using var indexed = reopened.BeginReadTransaction();
        indexed.Schema.ListIndexes()
            .Single(x => x.Name == "idx_external_id")
            .State.Should().Be(IndexLifecycleState.Ready);
        Seek(indexed, "idx_external_id", "doc-1")
            .Should().Equal(EntityRef.From(vertex));
    }

    private static List<EntityRef> Seek(
        IReadTransaction transaction,
        string indexName,
        string value)
    {
        using var hits = transaction.SeekIndex(
            indexName,
            PropertyValue.FromString(value));
        var result = new List<EntityRef>();
        while (hits.MoveNext())
            result.Add(hits.Current);
        return result;
    }

    private static List<EntityRef> Range(
        IReadTransaction transaction,
        string indexName,
        long from,
        long to)
    {
        using var hits = transaction.RangeIndex(
            indexName,
            PropertyValue.FromInt64(from),
            fromInclusive: true,
            PropertyValue.FromInt64(to),
            toInclusive: true);
        var result = new List<EntityRef>();
        while (hits.MoveNext())
            result.Add(hits.Current);
        return result;
    }

    private static PropertyVersionRef SinglePropertyVersion(
        IReadTransaction transaction,
        VertexId vertex)
    {
        PropertyCursor cursor = transaction.AsInternal().Inner.Vertices
            .EnumerateProperties(
                vertex,
                transaction.AsInternal().Inner.Properties);
        cursor.MoveNext().Should().BeTrue();
        PropertyVersionRef version = cursor.CurrentVersion;
        cursor.MoveNext().Should().BeFalse();
        return version;
    }
}
