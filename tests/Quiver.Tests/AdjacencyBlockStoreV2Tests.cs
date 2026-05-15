using FluentAssertions;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// BA-6 / codex_advice_3 §7.2: V2 adjacency view with an inline payload lane
/// must preserve weights across bulk-load and surface them through the expand
/// cursor / ExpandOperator's NeighborAndWeight projection. These tests cover
/// the round-trip plus the multi-page chain (V2's smaller per-entry slot
/// means ~370 entries per page versus V1's 581, so degrees above ~370 walk
/// multiple pages).
/// </summary>
public sealed class AdjacencyBlockStoreV2Tests : IDisposable
{
    private readonly string _dir;
    private GraphDatabase? _db;

    public AdjacencyBlockStoreV2Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_adj_v2_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Int64_payload_round_trips_through_v2_cursor()
    {
        const int degree = 10;
        var weightKey = BuildWithInt64Weights(degree);

        _db = GraphDatabase.Open(_dir);
        using var tx = _db.BeginTransaction();
        var adj = tx.AdjacencyBlocks;
        adj.Should().NotBeNull();

        var view = adj as IAdjacencyPayloadView;
        view.Should().NotBeNull("V2 adjacency view was built via WithPayloadLane");
        view!.PayloadSpec.Kind.Should().Be(PayloadKind.Int64);
        view.PayloadSpec.PropertyKeyId.Should().Be(weightKey.Value);

        using var cursor = adj!.OpenCursor(new NodeId(0), Direction.Outgoing, null);
        long sum = 0;
        int count = 0;
        while (cursor.MoveNext())
        {
            // Weight for edge i is 100 + i (set by BuildWithInt64Weights).
            cursor.WeightRaw.Should().Be(100 + cursor.Neighbor.Value);
            sum += cursor.WeightRaw;
            count++;
        }
        count.Should().Be(degree);
        // 100*deg + sum(1..deg)
        sum.Should().Be(100L * degree + degree * (degree + 1) / 2);
    }

    [Fact]
    public void Double_payload_round_trips_via_bitcast()
    {
        // Use a double key.
        using (var db = GraphDatabase.Open(_dir))
        {
            var key = db.Schema.GetOrCreatePropertyKey("score");
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.WithPayloadLane(PayloadLaneSpec.ForDouble(key.Value, defaultValue: double.NaN));
            loader.AppendNode(new NodeId(0), new LabelId(0));
            loader.AppendNode(new NodeId(1), new LabelId(0));
            loader.AppendNode(new NodeId(2), new LabelId(0));
            loader.AppendRelationship(new RelationshipId(0), new NodeId(0), new NodeId(1), new RelationshipTypeId(0));
            loader.AppendRelationship(new RelationshipId(1), new NodeId(0), new NodeId(2), new RelationshipTypeId(0));
            loader.AppendRelationshipPayload(new RelationshipId(0), key, BitConverter.DoubleToInt64Bits(1.5));
            loader.AppendRelationshipPayload(new RelationshipId(1), key, BitConverter.DoubleToInt64Bits(2.75));
            loader.Commit();
        }

        _db = GraphDatabase.Open(_dir);
        using var tx = _db.BeginTransaction();
        var seen = new Dictionary<long, double>();
        using var cursor = tx.AdjacencyBlocks!.OpenCursor(new NodeId(0), Direction.Outgoing, null);
        while (cursor.MoveNext())
            seen[cursor.Neighbor.Value] = BitConverter.Int64BitsToDouble(cursor.WeightRaw);

        seen.Should().HaveCount(2);
        seen[1].Should().Be(1.5);
        seen[2].Should().Be(2.75);
    }

    [Fact]
    public void Default_raw_applied_when_edge_has_no_payload()
    {
        using (var db = GraphDatabase.Open(_dir))
        {
            var key = db.Schema.GetOrCreatePropertyKey("weight");
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.WithPayloadLane(PayloadLaneSpec.ForInt64(key.Value, defaultValue: -42));
            loader.AppendNode(new NodeId(0), new LabelId(0));
            loader.AppendNode(new NodeId(1), new LabelId(0));
            loader.AppendNode(new NodeId(2), new LabelId(0));
            loader.AppendRelationship(new RelationshipId(0), new NodeId(0), new NodeId(1), new RelationshipTypeId(0));
            loader.AppendRelationship(new RelationshipId(1), new NodeId(0), new NodeId(2), new RelationshipTypeId(0));
            // Only edge 0 gets an explicit payload — edge 1 inherits the default.
            loader.AppendRelationshipPayload(new RelationshipId(0), key, 7);
            loader.Commit();
        }

        _db = GraphDatabase.Open(_dir);
        using var tx = _db.BeginTransaction();
        var seen = new Dictionary<long, long>();
        using var cursor = tx.AdjacencyBlocks!.OpenCursor(new NodeId(0), Direction.Outgoing, null);
        while (cursor.MoveNext())
            seen[cursor.Neighbor.Value] = cursor.WeightRaw;

        seen[1].Should().Be(7);
        seen[2].Should().Be(-42);
    }

    [Theory]
    [InlineData(50)]      // single page
    [InlineData(400)]     // just over one V2 page (~370 per page)
    [InlineData(2_000)]   // many V2 pages
    public void Weights_preserved_across_multi_page_chain(int degree)
    {
        BuildWithInt64Weights(degree);
        _db = GraphDatabase.Open(_dir);

        using var tx = _db.BeginTransaction();
        var seen = new Dictionary<long, long>();
        using var cursor = tx.AdjacencyBlocks!.OpenCursor(new NodeId(0), Direction.Outgoing, null);
        while (cursor.MoveNext())
            seen[cursor.Neighbor.Value] = cursor.WeightRaw;

        seen.Should().HaveCount(degree);
        for (int i = 1; i <= degree; i++)
            seen[i].Should().Be(100 + i);
    }

    [Fact]
    public void ExpandOperator_NeighborAndWeight_emits_weight_column()
    {
        const int degree = 5;
        BuildWithInt64Weights(degree);
        _db = GraphDatabase.Open(_dir);

        using var tx = _db.BeginTransaction();
        var op = new ExpandOperator(
            new SingleNodeSource(new NodeId(0)),
            sourceNodeColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborAndWeight);

        var result = tx.Execute(op);
        result.Schema.Columns.Should().HaveCount(3);
        result.Schema.Columns[2].Name.Should().Be("weight");
        result.Schema.Columns[2].Type.Should().Be(TupleSlotType.Int64);

        var rows = result.Rows().ToList();
        rows.Should().HaveCount(degree);
        foreach (var row in rows)
        {
            long n = row.GetNodeId(1).Value;
            long w = row.GetInt64(2);
            w.Should().Be(100 + n);
        }
    }

    [Fact]
    public void Reload_after_build_preserves_payload_spec()
    {
        BuildWithInt64Weights(3);

        // Close & reopen to ensure meta file persists.
        _db = GraphDatabase.Open(_dir);
        _db.Dispose();
        _db = GraphDatabase.Open(_dir);

        using var tx = _db.BeginTransaction();
        var view = tx.AdjacencyBlocks as IAdjacencyPayloadView;
        view.Should().NotBeNull();
        view!.PayloadSpec.Kind.Should().Be(PayloadKind.Int64);
        view.PayloadSpec.DefaultRaw.Should().Be(0);
    }

    private PropertyKeyId BuildWithInt64Weights(int degree)
    {
        using var db = GraphDatabase.Open(_dir);
        var key = db.Schema.GetOrCreatePropertyKey("weight");
        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        loader.WithPayloadLane(PayloadLaneSpec.ForInt64(key.Value));
        loader.AppendNode(new NodeId(0), new LabelId(0));
        for (int i = 1; i <= degree; i++)
        {
            loader.AppendNode(new NodeId(i), new LabelId(1));
            loader.AppendRelationship(new RelationshipId(i - 1),
                new NodeId(0), new NodeId(i), new RelationshipTypeId(0));
            loader.AppendRelationshipPayload(new RelationshipId(i - 1), key, 100 + i);
        }
        loader.Commit();
        return key;
    }

    /// <summary>
    /// Minimal source for unit-testing ExpandOperator without booting the
    /// rest of the operator stack. Emits exactly one NodeId row.
    /// </summary>
    private sealed class SingleNodeSource : IPhysicalOperator
    {
        private readonly TupleSlot[] _buf = new TupleSlot[1];
        private readonly NodeId _node;
        private bool _emitted;

        public SingleNodeSource(NodeId node) { _node = node; }
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.NodeId)]);
        public OperatorStatistics Statistics { get; private set; }
        public TupleRef Current => new(_buf);

        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }

        public bool MoveNext()
        {
            if (_emitted) return false;
            _buf[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _node.Value };
            _emitted = true;
            return true;
        }

        public void Dispose() { }
    }
}
