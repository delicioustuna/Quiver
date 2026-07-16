using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <see cref="IGraphKernel{TState}"/> と <see cref="OneHopExpansion"/> を
/// 利用する演算子をエンドツーエンドに検証する。
/// <see cref="BfsOperator"/>、<see cref="VariableLengthExpandOperator"/>、
/// <see cref="ShortestPathOperator"/>、並列 BFS 経路を対象とする。
/// </summary>
public sealed class GraphKernelTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public GraphKernelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_pw13_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ------------------------------------------------------------------ BFS --

    [Fact]
    public void BfsOperator_emits_every_reachable_vertex_with_correct_depth()
    {
        using var tx = _db.BeginTransaction();
        var person = _db.Schema.GetOrCreateLabel("Person");
        // Path: a -> b -> c -> d, plus a -> e (depth 1 fork).
        var a = tx.CreateVertex("Person");
        var b = tx.CreateVertex("Person");
        var c = tx.CreateVertex("Person");
        var d = tx.CreateVertex("Person");
        var e = tx.CreateVertex("Person");
        tx.CreateEdge(a, b, "K");
        tx.CreateEdge(b, c, "K");
        tx.CreateEdge(c, d, "K");
        tx.CreateEdge(a, e, "K");

        var src = new SingleVertexSource(a);
        using var bfs = new BfsOperator(src, 0, Direction.Outgoing, typeFilter: null, maxDepth: 3);
        using var result = tx.Execute(bfs);

        var rows = result.Rows()
            .Select(r => (end: r.GetVertexId(1).Value, depth: r.GetInt64(2)))
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
        var a = tx.CreateVertex("X");
        var b = tx.CreateVertex("X");
        var c = tx.CreateVertex("X");
        tx.CreateEdge(a, b, "K");
        tx.CreateEdge(b, c, "K");

        var src = new SingleVertexSource(a);
        using var bfs = new BfsOperator(src, 0, Direction.Outgoing, null, maxDepth: 1);
        using var result = tx.Execute(bfs);

        var rows = result.Rows().Select(r => r.GetVertexId(1).Value).ToList();
        rows.Should().Equal([b.Value]);
        tx.Rollback();
    }

    [Fact]
    public void BfsOperator_cycle_does_not_loop_forever()
    {
        using var tx = _db.BeginTransaction();
        // a -> b -> c -> a cycle.
        var a = tx.CreateVertex("X");
        var b = tx.CreateVertex("X");
        var c = tx.CreateVertex("X");
        tx.CreateEdge(a, b, "K");
        tx.CreateEdge(b, c, "K");
        tx.CreateEdge(c, a, "K");

        var src = new SingleVertexSource(a);
        using var bfs = new BfsOperator(src, 0, Direction.Outgoing, null, maxDepth: 10);
        using var result = tx.Execute(bfs);

        var rows = result.Rows().Select(r => r.GetVertexId(1).Value).ToHashSet();
        rows.Should().BeEquivalentTo([b.Value, c.Value]);
        tx.Rollback();
    }

    // ------------------------------------------ VariableLengthExpandOperator --

    [Fact]
    public void VariableLength_minHops_filters_short_paths()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateVertex("X");
        var b = tx.CreateVertex("X");
        var c = tx.CreateVertex("X");
        tx.CreateEdge(a, b, "K");
        tx.CreateEdge(b, c, "K");

        var src = new SingleVertexSource(a);
        // [minHops=2, maxHops=3]: must skip a (depth 0) and b (depth 1) — only c.
        using var vle = new VariableLengthExpandOperator(src, 0, Direction.Outgoing, null, minHops: 2, maxHops: 3);
        using var result = tx.Execute(vle);

        var rows = result.Rows().Select(r => r.GetVertexId(1).Value).ToList();
        rows.Should().Equal([c.Value]);
        tx.Rollback();
    }

    [Fact]
    public void VariableLength_minHops_zero_emits_start_vertex()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateVertex("X");
        var b = tx.CreateVertex("X");
        tx.CreateEdge(a, b, "K");

        var src = new SingleVertexSource(a);
        using var vle = new VariableLengthExpandOperator(src, 0, Direction.Outgoing, null, minHops: 0, maxHops: 1);
        using var result = tx.Execute(vle);

        var rows = result.Rows().Select(r => r.GetVertexId(1).Value).OrderBy(v => v).ToList();
        rows.Should().Equal([a.Value, b.Value]);
        tx.Rollback();
    }

    // -------------------------------------------- ShortestPathOperator (kernel
    //                                              early-abort path) ---------

    [Fact]
    public void ShortestPath_returns_correct_distance_and_aborts_early()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateVertex("X");
        var b = tx.CreateVertex("X");
        var c = tx.CreateVertex("X");
        var d = tx.CreateVertex("X");
        // a -> b -> c -> d (distance 3) plus a side branch a -> d directly via two hops.
        tx.CreateEdge(a, b, "K");
        tx.CreateEdge(b, c, "K");
        tx.CreateEdge(c, d, "K");
        tx.CreateEdge(a, c, "K"); // shortcut: a -> c (1 hop) -> d (2 hops total)

        var src = new SinglePairSource(a, d);
        using var sp = new ShortestPathOperator(src, 0, 1, Direction.Outgoing, null);
        using var result = tx.Execute(sp);

        var rows = result.Rows().Select(r => (src: r.GetVertexId(0).Value, dst: r.GetVertexId(1).Value, dist: r.GetInt64(2))).ToList();
        rows.Should().HaveCount(1);
        rows[0].dist.Should().Be(2);
        tx.Rollback();
    }

    [Fact]
    public void ShortestPath_returns_zero_for_self_pair()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateVertex("X");
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
        var a = tx.CreateVertex("X");
        var b = tx.CreateVertex("X");
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
        // ワーカー同士が相手のフロンティアを参照しないよう、独立した 2 本のチェーンを使う。
        var a = tx.CreateVertex("X");
        var b = tx.CreateVertex("X");
        var c = tx.CreateVertex("X");
        var d = tx.CreateVertex("X");
        var e = tx.CreateVertex("X");
        var f = tx.CreateVertex("X");
        tx.CreateEdge(a, b, "K");
        tx.CreateEdge(b, c, "K");
        tx.CreateEdge(d, e, "K");
        tx.CreateEdge(e, f, "K");

        // Same upstream definition for both runs so we compare apples-to-apples.
        IPhysicalOperator BuildSrc() => new MultiVertexSource([a, d]);

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
                .Select(row => (row.GetVertexId(0).Value, row.GetVertexId(1).Value, row.GetInt64(2)))
                .ToHashSet();
        }
    }

    // ----- minimal physical-operator stubs that drive the kernel from a
    //       fixed list of source vertices / source-target pairs ------------------

    private sealed class SingleVertexSource(VertexId vertex) : IPhysicalOperator
    {
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        private bool _emitted;
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.VertexId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }
        public bool MoveNext()
        {
            if (_emitted) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = vertex.Value };
            _emitted = true;
            return true;
        }
        public void Dispose() { }
    }

    private sealed class MultiVertexSource(VertexId[] vertices) : IPhysicalOperator
    {
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        private int _i = -1;
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.VertexId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(Quiver.Transactions.ITransaction tx) { _i = -1; }
        public bool MoveNext()
        {
            if (++_i >= vertices.Length) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = vertices[_i].Value };
            return true;
        }
        public void Dispose() { }
    }

    private sealed class SinglePairSource(VertexId src, VertexId tgt) : IPhysicalOperator
    {
        private readonly TupleSlot[] _buffer = new TupleSlot[2];
        private bool _emitted;
        public TupleSchema Schema { get; } = new([
            new ColumnDefinition("s", TupleSlotType.VertexId),
            new ColumnDefinition("t", TupleSlotType.VertexId),
        ]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }
        public bool MoveNext()
        {
            if (_emitted) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = src.Value };
            _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = tgt.Value };
            _emitted = true;
            return true;
        }
        public void Dispose() { }
    }
}
