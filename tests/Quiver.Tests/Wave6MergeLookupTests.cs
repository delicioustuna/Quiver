using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class Wave6MergeLookupTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "quiver_wave6_merge_" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public Wave6MergeLookupTests()
    {
        _path = Path.Combine(_directory, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void MergeEdge_rebuilds_lookup_after_reopen()
    {
        VertexId source;
        VertexId target;
        EdgeId created;
        using (var database = QuiverDatabase.Open(_path))
        using (var write = database.BeginWriteTransaction())
        {
            source = write.CreateVertex("Person");
            target = write.CreateVertex("Tool");
            (created, bool wasCreated) = write.MergeEdge(source, target, "USES");
            wasCreated.Should().BeTrue();
            write.Commit();
        }

        using var reopened = QuiverDatabase.Open(_path);
        using var second = reopened.BeginWriteTransaction();
        var found = second.MergeEdge(source, target, "USES");

        found.Should().Be((created, false));
        second.Commit();
    }

    [Fact]
    public void MergeEdge_revalidates_rolled_back_and_deleted_candidates()
    {
        VertexId source;
        VertexId target;
        using var database = QuiverDatabase.Open(_path);
        using (var seed = database.BeginWriteTransaction())
        {
            source = seed.CreateVertex("Person");
            target = seed.CreateVertex("Tool");
            seed.Commit();
        }

        using (var aborted = database.BeginWriteTransaction())
        {
            aborted.MergeEdge(source, target, "USES");
            aborted.Rollback();
        }

        EdgeId committed;
        using (var write = database.BeginWriteTransaction())
        {
            (committed, bool wasCreated) = write.MergeEdge(source, target, "USES");
            wasCreated.Should().BeTrue();
            write.Commit();
        }

        using (var delete = database.BeginWriteTransaction())
        {
            delete.DeleteEdge(committed);
            delete.Commit();
        }

        using var replacement = database.BeginWriteTransaction();
        var result = replacement.MergeEdge(source, target, "USES");
        result.Created.Should().BeTrue();
        result.Id.Should().NotBe(committed);
        replacement.Commit();
    }

    [Fact]
    public void MergeNexus_is_order_independent_and_rebuilds_lookup_after_reopen()
    {
        VertexId subject;
        VertexId @object;
        NexusId created;
        using (var database = QuiverDatabase.Open(_path))
        using (var write = database.BeginWriteTransaction())
        {
            subject = write.CreateVertex("Person");
            @object = write.CreateVertex("Document");
            (created, bool wasCreated) = write.MergeNexus(
                "Fact",
                [new("subject", subject), new("object", @object)]);
            wasCreated.Should().BeTrue();

            var same = write.MergeNexus(
                "Fact",
                [new("object", @object), new("subject", subject)]);
            same.Should().Be((created, false));
            write.Commit();
        }

        using var reopened = QuiverDatabase.Open(_path);
        using var second = reopened.BeginWriteTransaction();
        var found = second.MergeNexus(
            "Fact",
            [new("object", @object), new("subject", subject)]);

        found.Should().Be((created, false));
        second.Commit();
    }

    [Fact]
    public void MergeNexus_revalidates_rolled_back_and_deleted_candidates()
    {
        VertexId subject;
        VertexId @object;
        using var database = QuiverDatabase.Open(_path);
        using (var seed = database.BeginWriteTransaction())
        {
            subject = seed.CreateVertex("Person");
            @object = seed.CreateVertex("Document");
            seed.Commit();
        }

        using (var aborted = database.BeginWriteTransaction())
        {
            aborted.MergeNexus(
                "Fact",
                [new("subject", subject), new("object", @object)]);
            aborted.Rollback();
        }

        NexusId committed;
        using (var write = database.BeginWriteTransaction())
        {
            (committed, bool wasCreated) = write.MergeNexus(
                "Fact",
                [new("subject", subject), new("object", @object)]);
            wasCreated.Should().BeTrue();
            write.Commit();
        }

        using (var delete = database.BeginWriteTransaction())
        {
            delete.DeleteNexus(committed);
            delete.Commit();
        }

        using var replacement = database.BeginWriteTransaction();
        var result = replacement.MergeNexus(
            "Fact",
            [new("subject", subject), new("object", @object)]);
        result.Created.Should().BeTrue();
        result.Id.Should().NotBe(committed);
        replacement.Commit();
    }

    [Fact]
    public void Read_transaction_resolves_vertex_edge_and_nexus_type_from_primary_records()
    {
        using var database = QuiverDatabase.Open(_path);
        VertexId person;
        EdgeId edge;
        NexusId nexus;
        using (var write = database.BeginWriteTransaction())
        {
            person = write.CreateVertex("Person");
            VertexId document = write.CreateVertex("Document");
            edge = write.CreateEdge(person, document, "AUTHORED");
            nexus = write.CreateNexus(
                "Fact",
                [new("subject", person), new("object", document)]);
            write.Commit();
        }

        using var read = database.BeginReadTransaction();
        read.GetVertexLabel(person).Should().Be("Person");
        read.GetEdgeType(edge).Should().Be("AUTHORED");
        read.GetNexusType(nexus).Should().Be("Fact");
        read.GetEdgeType(new EdgeId(long.MaxValue)).Should().BeNull();
        read.GetNexusType(new NexusId(long.MaxValue)).Should().BeNull();
    }
}
