using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>実際の <c>QuiverDatabase</c> を使って <c>EdgeScanExpandOperator</c> を検証する。</summary>
public sealed class EdgeScanExpandOperatorTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public EdgeScanExpandOperatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_pw17_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void NeighborOnly_outgoing_matches_ExpandOperator()
    {
        var person = _db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var carol = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob,   "KNOWS");
        tx.CreateEdge(alice, carol, "KNOWS");
        tx.CreateEdge(bob,   carol, "KNOWS");

        var src = new VertexByLabelScanOperator(person);
        using var scan = new EdgeScanExpandOperator(
            src, sourceVertexColumn: 0, Direction.Outgoing, typeFilter: null,
            ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        var neighbors = result.Rows().Select(r => r.GetVertexId(0)).OrderBy(n => n.Value).ToList();
        neighbors.Should().BeEquivalentTo(new[] { bob, carol, carol });
        tx.Rollback();
    }

    [Fact]
    public void Full_outgoing_emits_source_edge_neighbor()
    {
        var person = _db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var edge = tx.CreateEdge(alice, bob, "KNOWS");

        var src = new VertexByLabelScanOperator(person);
        using var scan = new EdgeScanExpandOperator(
            src, 0, Direction.Outgoing, null, ExpandOutputMode.Full);
        using var result = tx.Execute(scan);

        var rows = result.Rows().ToList();
        rows.Should().HaveCount(1);
        rows[0].GetVertexId(0).Should().Be(alice);
        rows[0].GetEdgeId(1).Should().Be(edge);
        rows[0].GetVertexId(2).Should().Be(bob);
        tx.Rollback();
    }

    [Fact]
    public void Stale_full_frontier_does_not_expand_reused_vertex_slot()
    {
        VertexId stale;
        using (var write = _db.BeginWriteTransaction())
        {
            stale = write.CreateVertex("Person");
            write.Commit();
        }
        using (var write = _db.BeginWriteTransaction())
        {
            write.DeleteVertex(stale);
            write.Commit();
        }
        _db.Vacuum().ReclaimedVertices.Should().Be(1);

        VertexId replacement;
        using (var write = _db.BeginWriteTransaction())
        {
            replacement = write.CreateVertex("Person");
            var neighbor = write.CreateVertex("Person");
            write.CreateEdge(replacement, neighbor, "KNOWS");
            write.Commit();
        }
        replacement.Sequence.Should().Be(stale.Sequence);
        replacement.Generation.Should().NotBe(stale.Generation);

        using var read = _db.BeginReadTransaction();
        using var result = read.Execute(new EdgeScanExpandOperator(
            new FixedVertexListOperatorForPw17([stale]),
            sourceVertexColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborOnly));

        result.Rows().Should().BeEmpty(
            "a stale logical frontier must not retarget the reused physical slot");
    }

    [Fact]
    public void Stale_full_source_does_not_enter_binary_adjacency_cursor()
    {
        VertexId stale;
        using (var write = _db.BeginWriteTransaction())
        {
            stale = write.CreateVertex("Person");
            write.Commit();
        }
        using (var write = _db.BeginWriteTransaction())
        {
            write.DeleteVertex(stale);
            write.Commit();
        }
        _db.Vacuum().ReclaimedVertices.Should().Be(1);

        using (var write = _db.BeginWriteTransaction())
        {
            var replacement = write.CreateVertex("Person");
            var neighbor = write.CreateVertex("Person");
            replacement.Sequence.Should().Be(stale.Sequence);
            write.CreateEdge(replacement, neighbor, "KNOWS");
            write.Commit();
        }

        using var read = _db.BeginReadTransaction();
        using var result = read.Execute(new ExpandOperator(
            new FixedVertexListOperatorForPw17([stale]),
            sourceVertexColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborOnly));

        result.Rows().Should().BeEmpty(
            "the binary cursor must validate a full ID before using its Sequence as an adjacency key");
    }

    [Fact]
    public void TypeFilter_excludes_other_types()
    {
        var person = _db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var carol = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob,   "KNOWS");
        tx.CreateEdge(alice, carol, "BLOCKS");
        var knows = tx.EditSchema.GetOrCreateEdgeType("KNOWS");

        var src = new VertexByLabelScanOperator(person);
        using var scan = new EdgeScanExpandOperator(
            src, 0, Direction.Outgoing, knows, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        var neighbors = result.Rows().Select(r => r.GetVertexId(0)).ToList();
        neighbors.Should().BeEquivalentTo(new[] { bob });
        tx.Rollback();
    }

    [Fact]
    public void Incoming_direction_matches_targets()
    {
        var person = _db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");

        // Frontier = {bob}; Incoming from bob's perspective means edge.Target == bob.
        var src = new FixedVertexListOperatorForPw17([bob]);
        using var scan = new EdgeScanExpandOperator(
            src, 0, Direction.Incoming, null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        result.Rows().Select(r => r.GetVertexId(0)).Should().BeEquivalentTo(new[] { alice });
        tx.Rollback();
    }

    [Fact]
    public void Both_direction_emits_each_neighbor_once()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");

        var src = new FixedVertexListOperatorForPw17([alice]);
        using var scan = new EdgeScanExpandOperator(
            src, 0, Direction.Both, null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        result.Rows().Select(r => r.GetVertexId(0)).Should().BeEquivalentTo(new[] { bob });
        tx.Rollback();
    }

    [Fact]
    public void Statistics_count_RowsProduced_and_ScanRecords()
    {
        var person = _db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var carol = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob,   "KNOWS");
        tx.CreateEdge(bob,   carol, "KNOWS");

        var src = new FixedVertexListOperatorForPw17([alice]);
        using var scan = new EdgeScanExpandOperator(
            src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);
        var rows = result.Rows().ToList();

        rows.Should().HaveCount(1);
        scan.Statistics.RowsProduced.Should().Be(1);
        // Scan inspected both edges, even though only one matched the frontier.
        scan.Statistics.EdgeScanRecords.Should().Be(2);
        tx.Rollback();
    }

    [Fact]
    public void Empty_frontier_emits_nothing()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");

        var src = new FixedVertexListOperatorForPw17([]);
        using var scan = new EdgeScanExpandOperator(
            src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }
}

internal sealed class FixedVertexListOperatorForPw17 : IPhysicalOperator
{
    private readonly VertexId[] _vertices;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FixedVertexListOperatorForPw17(VertexId[] vertices) => _vertices = vertices;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(Quiver.Transactions.ITransaction tx) { _index = -1; }
    public bool MoveNext()
    {
        if (++_index >= _vertices.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _vertices[_index].Value };
        return true;
    }
    public void Dispose() { }
}
