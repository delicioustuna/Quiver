using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// PW-13: end-to-end coverage of <see cref="IGraphKernel{TState}"/> +
/// <see cref="OneHopExpansion"/> through the operators that were ported to
/// the kernel shell — <see cref="BfsOperator"/>,
/// <see cref="VariableLengthExpandOperator"/>, <see cref="ShortestPathOperator"/>
/// and the parallel BFS path.
/// </summary>
public sealed class GraphKernelTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public GraphKernelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_pw13_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(_dir);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ------------------------------------------------------------------ BFS --

    [Fact]
    public void BfsOperator_emits_every_reachable_node_with_correct_depth()
    {
        using var tx = _db.BeginTransaction();
        var person = _db.Schema.GetOrCreateLabel("Person");
        // Path: a -> b -> c -> d, plus a -> e (depth 1 fork).
        var a = tx.CreateNode("Person");
        var b = tx.CreateNode("Person");
        var c = tx.CreateNode("Person");
        var d = tx.CreateNode("Person");
        var e = tx.CreateNode("Person");
        tx.CreateRelationship(a, b, "K");
        tx.CreateRelationship(b, c, "K");
        tx.CreateRelationship(c, d, "K");
        tx.CreateRelationship(a, e, "K");

        var src = new SingleNodeSource(a);
        using var bfs = new BfsOperator(src, 0, Direction.Outgoing, typeFilter: null, maxDepth: 3);
        using var result = tx.Execute(bfs);

        var rows = result.Rows()
            .Select(r => (end: r.GetNodeId(1).Value, depth: r.GetInt64(2)))
            .OrderBy(x => x.depth).ThenBy(x => x.end)
            .ToList();

        rows.Should().BeEquivalentTo(new[]
        {
            (b.Value, 1L),
            (e.Value, 1L),
            (c.Value, 2L),
            (d.Value, 3L),
        });
        tx.Rollback();
    }

    [Fact]
    public void BfsOperator_caps_at_max_depth()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        tx.CreateRelationship(a, b, "K");
        tx.CreateRelationship(b, c, "K");

        var src = new SingleNodeSource(a);
        using var bfs = new BfsOperator(src, 0, Direction.Outgoing, null, maxDepth: 1);
        using var result = tx.Execute(bfs);

        var rows = result.Rows().Select(r => r.GetNodeId(1).Value).ToList();
        rows.Should().Equal([b.Value]);
        tx.Rollback();
    }

    [Fact]
    public void BfsOperator_cycle_does_not_loop_forever()
    {
        using var tx = _db.BeginTransaction();
        // a -> b -> c -> a cycle.
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        tx.CreateRelationship(a, b, "K");
        tx.CreateRelationship(b, c, "K");
        tx.CreateRelationship(c, a, "K");

        var src = new SingleNodeSource(a);
        using var bfs = new BfsOperator(src, 0, Direction.Outgoing, null, maxDepth: 10);
        using var result = tx.Execute(bfs);

        var rows = result.Rows().Select(r => r.GetNodeId(1).Value).ToHashSet();
        rows.Should().BeEquivalentTo([b.Value, c.Value]);
        tx.Rollback();
    }

    // ------------------------------------------ VariableLengthExpandOperator --

    [Fact]
    public void VariableLength_minHops_filters_short_paths()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        tx.CreateRelationship(a, b, "K");
        tx.CreateRelationship(b, c, "K");

        var src = new SingleNodeSource(a);
        // [minHops=2, maxHops=3]: must skip a (depth 0) and b (depth 1) — only c.
        using var vle = new VariableLengthExpandOperator(src, 0, Direction.Outgoing, null, minHops: 2, maxHops: 3);
        using var result = tx.Execute(vle);

        var rows = result.Rows().Select(r => r.GetNodeId(1).Value).ToList();
        rows.Should().Equal([c.Value]);
        tx.Rollback();
    }

    [Fact]
    public void VariableLength_minHops_zero_emits_start_node()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        tx.CreateRelationship(a, b, "K");

        var src = new SingleNodeSource(a);
        using var vle = new VariableLengthExpandOperator(src, 0, Direction.Outgoing, null, minHops: 0, maxHops: 1);
        using var result = tx.Execute(vle);

        var rows = result.Rows().Select(r => r.GetNodeId(1).Value).OrderBy(v => v).ToList();
        rows.Should().Equal([a.Value, b.Value]);
        tx.Rollback();
    }

    // -------------------------------------------- ShortestPathOperator (kernel
    //                                              early-abort path) ---------

    [Fact]
    public void ShortestPath_returns_correct_distance_and_aborts_early()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        var d = tx.CreateNode("X");
        // a -> b -> c -> d (distance 3) plus a side branch a -> d directly via two hops.
        tx.CreateRelationship(a, b, "K");
        tx.CreateRelationship(b, c, "K");
        tx.CreateRelationship(c, d, "K");
        tx.CreateRelationship(a, c, "K"); // shortcut: a -> c (1 hop) -> d (2 hops total)

        var src = new SinglePairSource(a, d);
        using var sp = new ShortestPathOperator(src, 0, 1, Direction.Outgoing, null);
        using var result = tx.Execute(sp);

        var rows = result.Rows().Select(r => (src: r.GetNodeId(0).Value, dst: r.GetNodeId(1).Value, dist: r.GetInt64(2))).ToList();
        rows.Should().HaveCount(1);
        rows[0].dist.Should().Be(2);
        tx.Rollback();
    }

    [Fact]
    public void ShortestPath_returns_zero_for_self_pair()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var src = new SinglePairSource(a, a);
        using var sp = new ShortestPathOperator(src, 0, 1, Direction.Outgoing, null);
        using var result = tx.Execute(sp);
        var rows = result.Rows().ToList();
        rows.Should().HaveCount(1);
        rows[0].GetInt64(2).Should().Be(0);
        tx.Rollback();
    }

    [Fact]
    public void ShortestPath_no_path_emits_no_row()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        // No edges between a and b.
        var src = new SinglePairSource(a, b);
        using var sp = new ShortestPathOperator(src, 0, 1, Direction.Outgoing, null);
        using var result = tx.Execute(sp);
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    // -------------------------------------------- parallel BFS path (kernel
    //                                              shared by N workers) -----

    [Fact]
    public void ParallelBfs_matches_sequential_BFS_for_multiple_sources()
    {
        using var tx = _db.BeginTransaction();
        // Two disjoint chains so worker tasks never observe each other's frontier.
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");
        var d = tx.CreateNode("X");
        var e = tx.CreateNode("X");
        var f = tx.CreateNode("X");
        tx.CreateRelationship(a, b, "K");
        tx.CreateRelationship(b, c, "K");
        tx.CreateRelationship(d, e, "K");
        tx.CreateRelationship(e, f, "K");

        // Same upstream definition for both runs so we compare apples-to-apples.
        IPhysicalOperator BuildSrc() => new MultiNodeSource([a, d]);

        using var sequential = new BfsOperator(BuildSrc(), 0, Direction.Outgoing, null, maxDepth: 2, maxParallelism: 1);
        using var parallel   = new BfsOperator(BuildSrc(), 0, Direction.Outgoing, null, maxDepth: 2, maxParallelism: -1);

        var seqRows = Drain(tx, sequential);
        var parRows = Drain(tx, parallel);

        parRows.Should().BeEquivalentTo(seqRows);
        tx.Rollback();

        static HashSet<(long start, long end, long depth)> Drain(IGraphTransaction tx, BfsOperator op)
        {
            using var r = tx.Execute(op);
            return r.Rows()
                .Select(row => (row.GetNodeId(0).Value, row.GetNodeId(1).Value, row.GetInt64(2)))
                .ToHashSet();
        }
    }

    // ----- minimal physical-operator stubs that drive the kernel from a
    //       fixed list of source nodes / source-target pairs ------------------

    private sealed class SingleNodeSource(NodeId node) : IPhysicalOperator
    {
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        private bool _emitted;
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.NodeId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }
        public bool MoveNext()
        {
            if (_emitted) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = node.Value };
            _emitted = true;
            return true;
        }
        public void Dispose() { }
    }

    private sealed class MultiNodeSource(NodeId[] nodes) : IPhysicalOperator
    {
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        private int _i = -1;
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.NodeId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(Quiver.Transactions.ITransaction tx) { _i = -1; }
        public bool MoveNext()
        {
            if (++_i >= nodes.Length) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = nodes[_i].Value };
            return true;
        }
        public void Dispose() { }
    }

    private sealed class SinglePairSource(NodeId src, NodeId tgt) : IPhysicalOperator
    {
        private readonly TupleSlot[] _buffer = new TupleSlot[2];
        private bool _emitted;
        public TupleSchema Schema { get; } = new([
            new ColumnDefinition("s", TupleSlotType.NodeId),
            new ColumnDefinition("t", TupleSlotType.NodeId),
        ]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }
        public bool MoveNext()
        {
            if (_emitted) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = src.Value };
            _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = tgt.Value };
            _emitted = true;
            return true;
        }
        public void Dispose() { }
    }
}
