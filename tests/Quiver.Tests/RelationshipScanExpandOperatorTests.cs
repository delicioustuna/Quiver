using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>実際の <c>GraphDatabase</c> を使って <c>RelationshipScanExpandOperator</c> を検証する。</summary>
public sealed class RelationshipScanExpandOperatorTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public RelationshipScanExpandOperatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_pw17_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void NeighborOnly_outgoing_matches_ExpandOperator()
    {
        using var tx = _db.BeginTransaction();
        var person = _db.Schema.GetOrCreateLabel("Person");
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var carol = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob,   "KNOWS");
        tx.CreateRelationship(alice, carol, "KNOWS");
        tx.CreateRelationship(bob,   carol, "KNOWS");

        var src = new NodeByLabelScanOperator(person);
        using var scan = new RelationshipScanExpandOperator(
            src, sourceNodeColumn: 0, Direction.Outgoing, typeFilter: null,
            ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        var neighbors = result.Rows().Select(r => r.GetNodeId(0)).OrderBy(n => n.Value).ToList();
        neighbors.Should().BeEquivalentTo(new[] { bob, carol, carol });
        tx.Rollback();
    }

    [Fact]
    public void Full_outgoing_emits_source_rel_neighbor()
    {
        using var tx = _db.BeginTransaction();
        var person = _db.Schema.GetOrCreateLabel("Person");
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var relationship = tx.CreateRelationship(alice, bob, "KNOWS");

        var src = new NodeByLabelScanOperator(person);
        using var scan = new RelationshipScanExpandOperator(
            src, 0, Direction.Outgoing, null, ExpandOutputMode.Full);
        using var result = tx.Execute(scan);

        var rows = result.Rows().ToList();
        rows.Should().HaveCount(1);
        rows[0].GetNodeId(0).Should().Be(alice);
        rows[0].GetRelationshipId(1).Should().Be(relationship);
        rows[0].GetNodeId(2).Should().Be(bob);
        tx.Rollback();
    }

    [Fact]
    public void Stale_full_frontier_does_not_expand_reused_node_slot()
    {
        NodeId stale;
        using (var write = _db.BeginTransaction())
        {
            stale = write.CreateNode("Person");
            write.Commit();
        }
        using (var write = _db.BeginTransaction())
        {
            write.DeleteNode(stale);
            write.Commit();
        }
        _db.Vacuum().ReclaimedNodes.Should().Be(1);

        NodeId replacement;
        using (var write = _db.BeginTransaction())
        {
            replacement = write.CreateNode("Person");
            var neighbor = write.CreateNode("Person");
            write.CreateRelationship(replacement, neighbor, "KNOWS");
            write.Commit();
        }
        replacement.Sequence.Should().Be(stale.Sequence);
        replacement.Generation.Should().NotBe(stale.Generation);

        using var read = _db.BeginReadOnlyTransaction();
        using var result = read.Execute(new RelationshipScanExpandOperator(
            new FixedNodeListOperatorForPw17([stale]),
            sourceNodeColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborOnly));

        result.Rows().Should().BeEmpty(
            "a stale logical frontier must not retarget the reused physical slot");
    }

    [Fact]
    public void Stale_full_source_does_not_enter_binary_adjacency_cursor()
    {
        NodeId stale;
        using (var write = _db.BeginTransaction())
        {
            stale = write.CreateNode("Person");
            write.Commit();
        }
        using (var write = _db.BeginTransaction())
        {
            write.DeleteNode(stale);
            write.Commit();
        }
        _db.Vacuum().ReclaimedNodes.Should().Be(1);

        using (var write = _db.BeginTransaction())
        {
            var replacement = write.CreateNode("Person");
            var neighbor = write.CreateNode("Person");
            replacement.Sequence.Should().Be(stale.Sequence);
            write.CreateRelationship(replacement, neighbor, "KNOWS");
            write.Commit();
        }

        using var read = _db.BeginReadOnlyTransaction();
        using var result = read.Execute(new ExpandOperator(
            new FixedNodeListOperatorForPw17([stale]),
            sourceNodeColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborOnly));

        result.Rows().Should().BeEmpty(
            "the binary cursor must validate a full ID before using its Sequence as an adjacency key");
    }

    [Fact]
    public void TypeFilter_excludes_other_types()
    {
        using var tx = _db.BeginTransaction();
        var person = _db.Schema.GetOrCreateLabel("Person");
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var carol = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob,   "KNOWS");
        tx.CreateRelationship(alice, carol, "BLOCKS");
        var knows = _db.Schema.GetOrCreateRelationshipType("KNOWS");

        var src = new NodeByLabelScanOperator(person);
        using var scan = new RelationshipScanExpandOperator(
            src, 0, Direction.Outgoing, knows, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        var neighbors = result.Rows().Select(r => r.GetNodeId(0)).ToList();
        neighbors.Should().BeEquivalentTo(new[] { bob });
        tx.Rollback();
    }

    [Fact]
    public void Incoming_direction_matches_targets()
    {
        using var tx = _db.BeginTransaction();
        var person = _db.Schema.GetOrCreateLabel("Person");
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");

        // Frontier = {bob}; Incoming from bob's perspective means rel.Target == bob.
        var src = new FixedNodeListOperatorForPw17([bob]);
        using var scan = new RelationshipScanExpandOperator(
            src, 0, Direction.Incoming, null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        result.Rows().Select(r => r.GetNodeId(0)).Should().BeEquivalentTo(new[] { alice });
        tx.Rollback();
    }

    [Fact]
    public void Both_direction_emits_each_neighbor_once()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");

        var src = new FixedNodeListOperatorForPw17([alice]);
        using var scan = new RelationshipScanExpandOperator(
            src, 0, Direction.Both, null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        result.Rows().Select(r => r.GetNodeId(0)).Should().BeEquivalentTo(new[] { bob });
        tx.Rollback();
    }

    [Fact]
    public void Statistics_count_RowsProduced_and_ScanRecords()
    {
        using var tx = _db.BeginTransaction();
        var person = _db.Schema.GetOrCreateLabel("Person");
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var carol = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob,   "KNOWS");
        tx.CreateRelationship(bob,   carol, "KNOWS");

        var src = new FixedNodeListOperatorForPw17([alice]);
        using var scan = new RelationshipScanExpandOperator(
            src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);
        var rows = result.Rows().ToList();

        rows.Should().HaveCount(1);
        scan.Statistics.RowsProduced.Should().Be(1);
        // Scan inspected both edges, even though only one matched the frontier.
        scan.Statistics.RelationshipScanRecords.Should().Be(2);
        tx.Rollback();
    }

    [Fact]
    public void Empty_frontier_emits_nothing()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");

        var src = new FixedNodeListOperatorForPw17([]);
        using var scan = new RelationshipScanExpandOperator(
            src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(scan);

        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }
}

internal sealed class FixedNodeListOperatorForPw17 : IPhysicalOperator
{
    private readonly NodeId[] _nodes;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FixedNodeListOperatorForPw17(NodeId[] nodes) => _nodes = nodes;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(Quiver.Transactions.ITransaction tx) { _index = -1; }
    public bool MoveNext()
    {
        if (++_index >= _nodes.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _nodes[_index].Value };
        return true;
    }
    public void Dispose() { }
}
