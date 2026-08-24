using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Storage.Records.Tests;

public sealed class EdgeDeltaStoreTests : IDisposable
{
    private readonly string[] _paths = Enumerable.Range(0, 2)
        .Select(_ => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        .ToArray();
    private readonly PagedFile _headFile;
    private readonly PagedFile _pageFile;
    private readonly EdgeDeltaHeadStore _heads;
    private readonly PersistentEdgeDeltaStore _store;

    public EdgeDeltaStoreTests()
    {
        _headFile = new PagedFile(_paths[0]);
        _pageFile = new PagedFile(_paths[1]);
        _heads = new EdgeDeltaHeadStore(_headFile);
        _store = new PersistentEdgeDeltaStore(_pageFile, _heads);
    }

    [Fact]
    public void Head_slot_round_trips_after_reopen()
    {
        var vertex = new VertexId(42);
        var edge = new EdgeId(7);

        _store.Append(vertex, Direction.Outgoing, edge, new VertexId(100), new EdgeTypeId(3));

        EdgeDeltaHead before = _heads.Get(vertex, Direction.Outgoing);
        before.EntryCount.Should().Be(1);
        before.FirstPageId.Should().Be(before.LastPageId);

        _headFile.Dispose();
        using var reopenedFile = new PagedFile(_paths[0]);
        var reopened = new EdgeDeltaHeadStore(reopenedFile);

        reopened.Get(vertex, Direction.Outgoing).Should().Be(before);
        reopened.Get(vertex, Direction.Incoming).Should().Be(EdgeDeltaHead.Empty);
    }

    [Fact]
    public void Cursor_reads_entries_in_append_order_across_page_rollover()
    {
        var vertex = new VertexId(1);
        int total = PersistentEdgeDeltaStore.EntriesPerPageForTest + 3;

        for (int i = 0; i < total; i++)
        {
            _store.Append(
                vertex,
                Direction.Outgoing,
                new EdgeId(i),
                new VertexId(1_000 + i),
                new EdgeTypeId(i % 2));
        }

        var actual = ReadAll(vertex, Direction.Outgoing);

        actual.Should().HaveCount(total);
        actual.Select(e => e.Edge.Sequence).Should().Equal(Enumerable.Range(0, total).Select(i => (long)i));
        actual.Select(e => e.Neighbor.Sequence).Should().Equal(Enumerable.Range(0, total).Select(i => 1_000L + i));
        _heads.Get(vertex, Direction.Outgoing).EntryCount.Should().Be(total);
        _heads.Get(vertex, Direction.Outgoing).FirstPageId.Should().NotBe(_heads.Get(vertex, Direction.Outgoing).LastPageId);
    }

    [Fact]
    public void Cursor_applies_type_filter_and_base_high_water_mark()
    {
        var vertex = new VertexId(2);
        for (int i = 0; i < 8; i++)
        {
            _store.Append(
                vertex,
                Direction.Outgoing,
                new EdgeId(i),
                new VertexId(10 + i),
                new EdgeTypeId(i % 2));
        }

        using var cursor = _store.OpenCursor(
            vertex,
            Direction.Outgoing,
            new EdgeTypeId(1),
            baseEdgeHwm: 4);
        var edges = new List<long>();
        while (cursor.MoveNext())
            edges.Add(cursor.Edge.Sequence);

        edges.Should().Equal(5, 7);
    }

    [Fact]
    public void Both_direction_reads_outgoing_then_incoming_without_duplication()
    {
        var vertex = new VertexId(3);
        _store.Append(vertex, Direction.Outgoing, new EdgeId(1), new VertexId(4), new EdgeTypeId(1));
        _store.Append(vertex, Direction.Incoming, new EdgeId(2), new VertexId(5), new EdgeTypeId(1));

        var actual = ReadAll(vertex, Direction.Both);

        actual.Select(e => e.Edge.Sequence).Should().Equal(1, 2);
        actual.Select(e => e.Neighbor.Sequence).Should().Equal(4, 5);
    }

    [Fact]
    public void Reset_clears_heads_and_pages_for_later_appends()
    {
        var vertex = new VertexId(4);
        _store.Append(vertex, Direction.Outgoing, new EdgeId(1), new VertexId(5), new EdgeTypeId(1));

        _store.Reset();

        _store.Count(vertex, Direction.Outgoing).Should().Be(0);
        _heads.Get(vertex, Direction.Outgoing).Should().Be(EdgeDeltaHead.Empty);

        _store.Append(vertex, Direction.Outgoing, new EdgeId(2), new VertexId(6), new EdgeTypeId(1));
        ReadAll(vertex, Direction.Outgoing).Single().Edge.Should().Be(new EdgeId(2));
    }

    private List<(EdgeId Edge, VertexId Neighbor, EdgeTypeId Type)> ReadAll(
        VertexId vertex,
        Direction direction)
    {
        using var cursor = _store.OpenCursor(vertex, direction);
        var result = new List<(EdgeId Edge, VertexId Neighbor, EdgeTypeId Type)>();
        while (cursor.MoveNext())
            result.Add((cursor.Edge, cursor.Neighbor, cursor.Type));
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
