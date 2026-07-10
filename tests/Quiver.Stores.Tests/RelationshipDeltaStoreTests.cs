using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

public sealed class RelationshipDeltaStoreTests : IDisposable
{
    private readonly string[] _paths = Enumerable.Range(0, 2)
        .Select(_ => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        .ToArray();
    private readonly PagedFile _headFile;
    private readonly PagedFile _pageFile;
    private readonly RelationshipDeltaHeadStore _heads;
    private readonly PersistentRelationshipDeltaStore _store;

    public RelationshipDeltaStoreTests()
    {
        _headFile = new PagedFile(_paths[0]);
        _pageFile = new PagedFile(_paths[1]);
        _heads = new RelationshipDeltaHeadStore(_headFile);
        _store = new PersistentRelationshipDeltaStore(_pageFile, _heads);
    }

    [Fact]
    public void Head_slot_round_trips_after_reopen()
    {
        var node = new NodeId(42);
        var rel = new RelationshipId(7);

        _store.Append(node, Direction.Outgoing, rel, new NodeId(100), new RelationshipTypeId(3));

        RelationshipDeltaHead before = _heads.Get(node, Direction.Outgoing);
        before.EntryCount.Should().Be(1);
        before.FirstPageId.Should().Be(before.LastPageId);

        _headFile.Dispose();
        using var reopenedFile = new PagedFile(_paths[0]);
        var reopened = new RelationshipDeltaHeadStore(reopenedFile);

        reopened.Get(node, Direction.Outgoing).Should().Be(before);
        reopened.Get(node, Direction.Incoming).Should().Be(RelationshipDeltaHead.Empty);
    }

    [Fact]
    public void Cursor_reads_entries_in_append_order_across_page_rollover()
    {
        var node = new NodeId(1);
        int total = PersistentRelationshipDeltaStore.EntriesPerPageForTest + 3;

        for (int i = 0; i < total; i++)
        {
            _store.Append(
                node,
                Direction.Outgoing,
                new RelationshipId(i),
                new NodeId(1_000 + i),
                new RelationshipTypeId(i % 2));
        }

        var actual = ReadAll(node, Direction.Outgoing);

        actual.Should().HaveCount(total);
        actual.Select(e => e.Relationship.Sequence).Should().Equal(Enumerable.Range(0, total).Select(i => (long)i));
        actual.Select(e => e.Neighbor.Sequence).Should().Equal(Enumerable.Range(0, total).Select(i => 1_000L + i));
        _heads.Get(node, Direction.Outgoing).EntryCount.Should().Be(total);
        _heads.Get(node, Direction.Outgoing).FirstPageId.Should().NotBe(_heads.Get(node, Direction.Outgoing).LastPageId);
    }

    [Fact]
    public void Cursor_applies_type_filter_and_base_high_water_mark()
    {
        var node = new NodeId(2);
        for (int i = 0; i < 8; i++)
        {
            _store.Append(
                node,
                Direction.Outgoing,
                new RelationshipId(i),
                new NodeId(10 + i),
                new RelationshipTypeId(i % 2));
        }

        using var cursor = _store.OpenCursor(
            node,
            Direction.Outgoing,
            new RelationshipTypeId(1),
            baseRelHwm: 4);
        var relationships = new List<long>();
        while (cursor.MoveNext())
            relationships.Add(cursor.Relationship.Sequence);

        relationships.Should().Equal(5, 7);
    }

    [Fact]
    public void Both_direction_reads_outgoing_then_incoming_without_duplication()
    {
        var node = new NodeId(3);
        _store.Append(node, Direction.Outgoing, new RelationshipId(1), new NodeId(4), new RelationshipTypeId(1));
        _store.Append(node, Direction.Incoming, new RelationshipId(2), new NodeId(5), new RelationshipTypeId(1));

        var actual = ReadAll(node, Direction.Both);

        actual.Select(e => e.Relationship.Sequence).Should().Equal(1, 2);
        actual.Select(e => e.Neighbor.Sequence).Should().Equal(4, 5);
    }

    [Fact]
    public void Reset_clears_heads_and_pages_for_later_appends()
    {
        var node = new NodeId(4);
        _store.Append(node, Direction.Outgoing, new RelationshipId(1), new NodeId(5), new RelationshipTypeId(1));

        _store.Reset();

        _store.Count(node, Direction.Outgoing).Should().Be(0);
        _heads.Get(node, Direction.Outgoing).Should().Be(RelationshipDeltaHead.Empty);

        _store.Append(node, Direction.Outgoing, new RelationshipId(2), new NodeId(6), new RelationshipTypeId(1));
        ReadAll(node, Direction.Outgoing).Single().Relationship.Should().Be(new RelationshipId(2));
    }

    private List<(RelationshipId Relationship, NodeId Neighbor, RelationshipTypeId Type)> ReadAll(
        NodeId node,
        Direction direction)
    {
        using var cursor = _store.OpenCursor(node, direction);
        var result = new List<(RelationshipId Relationship, NodeId Neighbor, RelationshipTypeId Type)>();
        while (cursor.MoveNext())
            result.Add((cursor.Relationship, cursor.Neighbor, cursor.Type));
        return result;
    }

    public void Dispose()
    {
        _headFile.Dispose();
        _pageFile.Dispose();
        foreach (string path in _paths)
            File.Delete(path);
    }
}
