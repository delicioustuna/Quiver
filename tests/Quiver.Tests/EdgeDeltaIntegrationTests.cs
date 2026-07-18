using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class EdgeDeltaIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quiver-edge-delta-" + Guid.NewGuid());
    private readonly string _path;

    public EdgeDeltaIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    [Fact]
    public void Create_edge_persists_outgoing_and_incoming_delta_entries()
    {
        EdgeId edgeId;
        VertexId source;
        VertexId target;

        using (var db = QuiverDatabase.Open(_path))
        {
            using var tx = db.BeginWriteTransaction();
            source = tx.CreateVertex("Vertex");
            target = tx.CreateVertex("Vertex");
            edgeId = tx.CreateEdge(source, target, "LINK");
            tx.Commit();
        }

        using var container = new SingleFileContainer(_path);
        var heads = new EdgeDeltaHeadStore(
            container.OpenTenant(BinaryGraphStorageBackendFactory.TenantEdgeDeltaHead, PageKind.Header));
        var deltas = new PersistentEdgeDeltaStore(
            container.OpenTenant(BinaryGraphStorageBackendFactory.TenantEdgeDeltaPages, PageKind.EdgeDeltaRecord),
            heads);

        ReadAll(deltas, source, Direction.Outgoing).Should().Equal(
            [(edgeId.Sequence, target.Sequence, 0)]);
        ReadAll(deltas, target, Direction.Incoming).Should().Equal(
            [(edgeId.Sequence, source.Sequence, 0)]);
    }

    [Fact]
    public void Create_self_loop_persists_single_delta_entry()
    {
        VertexId vertex;
        EdgeId edgeId;

        using (var db = QuiverDatabase.Open(_path))
        {
            using var tx = db.BeginWriteTransaction();
            vertex = tx.CreateVertex("Vertex");
            edgeId = tx.CreateEdge(vertex, vertex, "SELF");
            tx.Commit();
        }

        using var container = new SingleFileContainer(_path);
        var heads = new EdgeDeltaHeadStore(
            container.OpenTenant(BinaryGraphStorageBackendFactory.TenantEdgeDeltaHead, PageKind.Header));
        var deltas = new PersistentEdgeDeltaStore(
            container.OpenTenant(BinaryGraphStorageBackendFactory.TenantEdgeDeltaPages, PageKind.EdgeDeltaRecord),
            heads);

        ReadAll(deltas, vertex, Direction.Outgoing).Should().Equal(
            [(edgeId.Sequence, vertex.Sequence, 0)]);
        ReadAll(deltas, vertex, Direction.Incoming).Should().BeEmpty();
    }

    [Fact]
    public void Traversal_reads_persistent_delta_after_reopen()
    {
        VertexId source;
        VertexId target;

        using (var db = QuiverDatabase.Open(_path))
        using (var tx = db.BeginWriteTransaction())
        {
            source = tx.CreateVertex("Vertex");
            target = tx.CreateVertex("Vertex");
            tx.CreateEdge(source, target, "LINK");
            tx.Commit();
        }

        using var reopened = QuiverDatabase.Open(_path);
        using var read = reopened.BeginReadTransaction();
        read.Query.Vertex(source).Out("LINK").ToList()
            .Should().ContainSingle().Which.Should().Be(target);
        read.Query.Vertex(target).In("LINK").ToList()
            .Should().ContainSingle().Which.Should().Be(source);
    }

    [Fact]
    public void Persistent_delta_cursor_filters_deleted_edges_through_row_store()
    {
        VertexId source;
        VertexId deletedTarget;
        VertexId liveTarget;
        EdgeId deleted;

        using (var db = QuiverDatabase.Open(_path))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                source = tx.CreateVertex("Vertex");
                deletedTarget = tx.CreateVertex("Vertex");
                liveTarget = tx.CreateVertex("Vertex");
                deleted = tx.CreateEdge(source, deletedTarget, "LINK");
                tx.CreateEdge(source, liveTarget, "LINK");
                tx.Commit();
            }

            using (var tx = db.BeginWriteTransaction())
            {
                tx.DeleteEdge(deleted);
                tx.Commit();
            }
        }

        using var reopened = QuiverDatabase.Open(_path);
        using var read = reopened.BeginReadTransaction();
        read.Query.Vertex(source).Out("LINK").ToList()
            .Should().ContainSingle().Which.Should().Be(liveTarget);
    }

    [Fact]
    public void Incoming_traversal_reads_self_loop_single_delta_entry()
    {
        VertexId vertex;

        using (var db = QuiverDatabase.Open(_path))
        using (var tx = db.BeginWriteTransaction())
        {
            vertex = tx.CreateVertex("Vertex");
            tx.CreateEdge(vertex, vertex, "SELF");
            tx.Commit();
        }

        using var reopened = QuiverDatabase.Open(_path);
        using var read = reopened.BeginReadTransaction();
        read.Query.Vertex(vertex).In("SELF").ToList()
            .Should().ContainSingle().Which.Should().Be(vertex);
        read.Query.Vertex(vertex).Both("SELF").ToList()
            .Should().ContainSingle().Which.Should().Be(vertex);
    }

    [Fact]
    public void Compact_resets_persistent_delta_after_rebuilding_base()
    {
        VertexId source;
        VertexId target;

        using (var db = QuiverDatabase.Open(_path))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                source = tx.CreateVertex("Vertex");
                target = tx.CreateVertex("Vertex");
                tx.CreateEdge(source, target, "LINK");
                tx.Commit();
            }

            using (var beforeCompact = db.BeginReadTransaction())
            {
                beforeCompact.Query.Vertex(source).Out("LINK").ToList()
                    .Should().ContainSingle().Which.Should().Be(target);
            }

            db.CompactAdjacency();

            using var read = db.BeginReadTransaction();
            read.Query.Vertex(source).Out("LINK").ToList()
                .Should().ContainSingle().Which.Should().Be(target);
        }

        using (var container = new SingleFileContainer(_path))
        {
            var heads = new EdgeDeltaHeadStore(
                container.OpenTenant(BinaryGraphStorageBackendFactory.TenantEdgeDeltaHead, PageKind.Header));
            var deltas = new PersistentEdgeDeltaStore(
                container.OpenTenant(BinaryGraphStorageBackendFactory.TenantEdgeDeltaPages, PageKind.EdgeDeltaRecord),
                heads);

            heads.Get(source, Direction.Outgoing).Should().Be(EdgeDeltaHead.Empty);
            deltas.Count(source, Direction.Outgoing).Should().Be(0);
        }

        using var reopened = QuiverDatabase.Open(_path);
        using var readAfterReopen = reopened.BeginReadTransaction();
        readAfterReopen.Query.Vertex(source).Out("LINK").ToList()
            .Should().ContainSingle().Which.Should().Be(target);
    }

    private static List<(long EdgeSeq, long OtherSeq, int TypeId)> ReadAll(
        PersistentEdgeDeltaStore deltas,
        VertexId vertex,
        Direction direction)
    {
        using var cursor = deltas.OpenCursor(vertex, direction);
        var result = new List<(long EdgeSeq, long OtherSeq, int TypeId)>();
        while (cursor.MoveNext())
            result.Add((cursor.Edge.Sequence, cursor.Neighbor.Sequence, cursor.Type.Value));
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
