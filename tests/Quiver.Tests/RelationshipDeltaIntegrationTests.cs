using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class RelationshipDeltaIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quiver-rel-delta-" + Guid.NewGuid());
    private readonly string _path;

    public RelationshipDeltaIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    [Fact]
    public void Create_relationship_persists_outgoing_and_incoming_delta_entries()
    {
        RelationshipId relId;
        NodeId source;
        NodeId target;

        using (var db = GraphDatabase.Open(_path))
        {
            using var tx = db.BeginTransaction();
            source = tx.CreateNode("Node");
            target = tx.CreateNode("Node");
            relId = tx.CreateRelationship(source, target, "LINK");
            tx.Commit();
        }

        using var container = new SingleFileContainer(_path);
        var heads = new RelationshipDeltaHeadStore(
            container.OpenTenant(BinaryGraphStorageBackendFactory.TenantRelationshipDeltaHead, PageKind.Header));
        var deltas = new PersistentRelationshipDeltaStore(
            container.OpenTenant(BinaryGraphStorageBackendFactory.TenantRelationshipDeltaPages, PageKind.RelationshipDeltaRecord),
            heads);

        ReadAll(deltas, source, Direction.Outgoing).Should().Equal(
            [(relId.Sequence, target.Sequence, 0)]);
        ReadAll(deltas, target, Direction.Incoming).Should().Equal(
            [(relId.Sequence, source.Sequence, 0)]);
    }

    [Fact]
    public void Create_self_loop_persists_single_delta_entry()
    {
        NodeId node;
        RelationshipId relId;

        using (var db = GraphDatabase.Open(_path))
        {
            using var tx = db.BeginTransaction();
            node = tx.CreateNode("Node");
            relId = tx.CreateRelationship(node, node, "SELF");
            tx.Commit();
        }

        using var container = new SingleFileContainer(_path);
        var heads = new RelationshipDeltaHeadStore(
            container.OpenTenant(BinaryGraphStorageBackendFactory.TenantRelationshipDeltaHead, PageKind.Header));
        var deltas = new PersistentRelationshipDeltaStore(
            container.OpenTenant(BinaryGraphStorageBackendFactory.TenantRelationshipDeltaPages, PageKind.RelationshipDeltaRecord),
            heads);

        ReadAll(deltas, node, Direction.Outgoing).Should().Equal(
            [(relId.Sequence, node.Sequence, 0)]);
        ReadAll(deltas, node, Direction.Incoming).Should().BeEmpty();
    }

    [Fact]
    public void Traversal_reads_persistent_delta_after_reopen()
    {
        NodeId source;
        NodeId target;

        using (var db = GraphDatabase.Open(_path))
        using (var tx = db.BeginTransaction())
        {
            source = tx.CreateNode("Node");
            target = tx.CreateNode("Node");
            tx.CreateRelationship(source, target, "LINK");
            tx.Commit();
        }

        using var reopened = GraphDatabase.Open(_path);
        using var read = reopened.BeginReadOnlyTransaction();
        read.G(reopened.Schema).Node(source).Out("LINK").ToList()
            .Should().ContainSingle().Which.Should().Be(target);
        read.G(reopened.Schema).Node(target).In("LINK").ToList()
            .Should().ContainSingle().Which.Should().Be(source);
    }

    [Fact]
    public void Persistent_delta_cursor_filters_deleted_relationships_through_row_store()
    {
        NodeId source;
        NodeId deletedTarget;
        NodeId liveTarget;
        RelationshipId deleted;

        using (var db = GraphDatabase.Open(_path))
        {
            using (var tx = db.BeginTransaction())
            {
                source = tx.CreateNode("Node");
                deletedTarget = tx.CreateNode("Node");
                liveTarget = tx.CreateNode("Node");
                deleted = tx.CreateRelationship(source, deletedTarget, "LINK");
                tx.CreateRelationship(source, liveTarget, "LINK");
                tx.Commit();
            }

            using (var tx = db.BeginTransaction())
            {
                tx.DeleteRelationship(deleted);
                tx.Commit();
            }
        }

        using var reopened = GraphDatabase.Open(_path);
        using var read = reopened.BeginReadOnlyTransaction();
        read.G(reopened.Schema).Node(source).Out("LINK").ToList()
            .Should().ContainSingle().Which.Should().Be(liveTarget);
    }

    [Fact]
    public void Incoming_traversal_reads_self_loop_single_delta_entry()
    {
        NodeId node;

        using (var db = GraphDatabase.Open(_path))
        using (var tx = db.BeginTransaction())
        {
            node = tx.CreateNode("Node");
            tx.CreateRelationship(node, node, "SELF");
            tx.Commit();
        }

        using var reopened = GraphDatabase.Open(_path);
        using var read = reopened.BeginReadOnlyTransaction();
        read.G(reopened.Schema).Node(node).In("SELF").ToList()
            .Should().ContainSingle().Which.Should().Be(node);
        read.G(reopened.Schema).Node(node).Both("SELF").ToList()
            .Should().ContainSingle().Which.Should().Be(node);
    }

    [Fact]
    public void Compact_resets_persistent_delta_after_rebuilding_base()
    {
        NodeId source;
        NodeId target;

        using (var db = GraphDatabase.Open(_path))
        {
            using (var tx = db.BeginTransaction())
            {
                source = tx.CreateNode("Node");
                target = tx.CreateNode("Node");
                tx.CreateRelationship(source, target, "LINK");
                tx.Commit();
            }

            db.CompactAdjacency();

            using var read = db.BeginReadOnlyTransaction();
            read.G(db.Schema).Node(source).Out("LINK").ToList()
                .Should().ContainSingle().Which.Should().Be(target);
        }

        using (var container = new SingleFileContainer(_path))
        {
            var heads = new RelationshipDeltaHeadStore(
                container.OpenTenant(BinaryGraphStorageBackendFactory.TenantRelationshipDeltaHead, PageKind.Header));
            var deltas = new PersistentRelationshipDeltaStore(
                container.OpenTenant(BinaryGraphStorageBackendFactory.TenantRelationshipDeltaPages, PageKind.RelationshipDeltaRecord),
                heads);

            heads.Get(source, Direction.Outgoing).Should().Be(RelationshipDeltaHead.Empty);
            deltas.Count(source, Direction.Outgoing).Should().Be(0);
        }

        using var reopened = GraphDatabase.Open(_path);
        using var readAfterReopen = reopened.BeginReadOnlyTransaction();
        readAfterReopen.G(reopened.Schema).Node(source).Out("LINK").ToList()
            .Should().ContainSingle().Which.Should().Be(target);
    }

    private static List<(long RelSeq, long OtherSeq, int TypeId)> ReadAll(
        PersistentRelationshipDeltaStore deltas,
        NodeId node,
        Direction direction)
    {
        using var cursor = deltas.OpenCursor(node, direction);
        var result = new List<(long RelSeq, long OtherSeq, int TypeId)>();
        while (cursor.MoveNext())
            result.Add((cursor.Relationship.Sequence, cursor.Neighbor.Sequence, cursor.Type.Value));
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
