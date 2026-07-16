using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <c>StreamingBulkLoader</c> がメモリ内 <see cref="BulkLoader"/> と
/// 同じオンディスクデータベースを生成することを検証する。
/// 両者は同じ密ポインターアルゴリズムを使い、Edgeの入力元だけが
/// 一時ファイルとヒープ上のリストで異なるため、EdgeStore、VertexStore、
/// FirstEdgeId チェーンがバイト単位で一致することを確認する。
/// </summary>
public sealed class StreamingBulkLoaderTests : IDisposable
{
    private readonly string _baseDir;

    public StreamingBulkLoaderTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "quiver_streaming_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    [Fact]
    public void Empty_graph_commits_without_error()
    {
        var dir = Path.Combine(_baseDir, "empty");
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
        using (var loader = db.BeginStreamingBulkLoad())
            loader.Commit();

        using var reopened = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
        using var tx = reopened.BeginTransaction();
        // No vertices, no edges — but the db must be openable.
        tx.Should().NotBeNull();
    }

    [Fact]
    public void Streaming_matches_inmemory_for_simple_chain()
    {
        var edges = new[] { (0L, 1L), (1L, 2L), (2L, 3L), (0L, 3L) };
        AssertParity(vertexCount: 4, edges, payloadKey: null);
    }

    [Fact]
    public void Streaming_matches_inmemory_with_self_loop()
    {
        var edges = new[] { (0L, 0L), (0L, 1L), (1L, 1L) };
        AssertParity(vertexCount: 2, edges, payloadKey: null);
    }

    [Fact]
    public void Streaming_matches_inmemory_with_dense_random_edges()
    {
        var rng = new Random(42);
        const int vertexCount = 50;
        const int edgeCount = 500;
        var edges = new (long Src, long Tgt)[edgeCount];
        for (int i = 0; i < edgeCount; i++)
            edges[i] = (rng.Next(vertexCount), rng.Next(vertexCount));
        AssertParity(vertexCount, edges, payloadKey: null);
    }

    [Fact]
    public void Streaming_throws_when_appendedge_is_out_of_order()
    {
        var dir = Path.Combine(_baseDir, "oodo");
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
        using var loader = db.BeginStreamingBulkLoad();
        loader.AppendVertex(new VertexId(0), new LabelId(0));
        loader.AppendVertex(new VertexId(1), new LabelId(0));
        loader.AppendEdge(new EdgeId(5), new VertexId(0), new VertexId(1), new EdgeTypeId(0));

        Action act = () => loader.AppendEdge(
            new EdgeId(3), new VertexId(0), new VertexId(1), new EdgeTypeId(0));
        act.Should().Throw<InvalidOperationException>().WithMessage("*strictly increasing*");
    }

    [Fact]
    public void Streaming_with_adjacency_index_matches_inmemory()
    {
        var edges = new[] { (0L, 1L), (0L, 2L), (1L, 2L), (2L, 0L), (3L, 1L) };

        var inmemDir = Path.Combine(_baseDir, "adj_in");
        var streamDir = Path.Combine(_baseDir, "adj_st");
        Build(inmemDir, streaming: false, vertexCount: 4, edges, buildAdj: true);
        Build(streamDir, streaming: true,  vertexCount: 4, edges, buildAdj: true);

        // The adjacency index must produce identical Expand results for every source.
        using var dbA = QuiverDatabase.Open(System.IO.Path.Combine(inmemDir, "graph.quiver"));
        using var dbB = QuiverDatabase.Open(System.IO.Path.Combine(streamDir, "graph.quiver"));
        for (long n = 0; n < 4; n++)
        {
            using var txA = dbA.BeginTransaction();
            using var txB = dbB.BeginTransaction();
            var ea = ExpandOut(txA, new VertexId(n));
            var eb = ExpandOut(txB, new VertexId(n));
            eb.Should().BeEquivalentTo(ea, opts => opts.WithStrictOrdering(),
                $"vertex {n}: streaming and in-memory adjacency must yield the same neighbors in the same order");
        }
    }

    [Fact]
    public void Streaming_with_properties_produces_same_property_chain()
    {
        var dir1 = Path.Combine(_baseDir, "prop_in");
        var dir2 = Path.Combine(_baseDir, "prop_st");
        BuildWithProps(dir1, streaming: false);
        BuildWithProps(dir2, streaming: true);

        AssertVertexStoreBytesEqual(dir1, dir2);
        AssertEdgeStoreBytesEqual(dir1, dir2);
        AssertPropStoreBytesEqual(dir1, dir2);
    }

    // ─────────────────────── helpers ───────────────────────

    private void AssertParity(int vertexCount, (long Src, long Tgt)[] edges, string? payloadKey)
    {
        var inmemDir = Path.Combine(_baseDir, "parity_in_" + Guid.NewGuid().ToString("N")[..8]);
        var streamDir = Path.Combine(_baseDir, "parity_st_" + Guid.NewGuid().ToString("N")[..8]);
        Build(inmemDir, streaming: false, vertexCount, edges, buildAdj: false);
        Build(streamDir, streaming: true,  vertexCount, edges, buildAdj: false);

        // Files on disk must match byte-for-byte: same chain pointers, same FirstEdgeId.
        AssertVertexStoreBytesEqual(inmemDir, streamDir);
        AssertEdgeStoreBytesEqual(inmemDir, streamDir);
    }

    private static void Build(string dir, bool streaming, int vertexCount,
        (long Src, long Tgt)[] edges, bool buildAdj)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
        if (streaming)
        {
            using var loader = db.BeginStreamingBulkLoad(buildAdjacencyIndex: buildAdj);
            for (int i = 0; i < vertexCount; i++)
                loader.AppendVertex(new VertexId(i), new LabelId(0));
            for (int i = 0; i < edges.Length; i++)
                loader.AppendEdge(
                    new EdgeId(i),
                    new VertexId(edges[i].Src), new VertexId(edges[i].Tgt),
                    new EdgeTypeId(0));
            loader.Commit();
        }
        else
        {
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: buildAdj);
            for (int i = 0; i < vertexCount; i++)
                loader.AppendVertex(new VertexId(i), new LabelId(0));
            for (int i = 0; i < edges.Length; i++)
                loader.AppendEdge(
                    new EdgeId(i),
                    new VertexId(edges[i].Src), new VertexId(edges[i].Tgt),
                    new EdgeTypeId(0));
            loader.Commit();
        }
    }

    private static void BuildWithProps(string dir, bool streaming)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
        var keyName = db.Schema.GetOrCreatePropertyKey("name");
        var keyAge  = db.Schema.GetOrCreatePropertyKey("age");

        if (streaming)
        {
            using var loader = db.BeginStreamingBulkLoad();
            for (int i = 0; i < 3; i++)
                loader.AppendVertex(new VertexId(i), new LabelId(0));
            loader.AppendEdge(new EdgeId(0), new VertexId(0), new VertexId(1), new EdgeTypeId(0));
            loader.AppendEdge(new EdgeId(1), new VertexId(1), new VertexId(2), new EdgeTypeId(0));
            loader.AppendProperty(new VertexId(0), keyName, PropertyValue.FromString("alice"));
            loader.AppendProperty(new VertexId(0), keyAge,  PropertyValue.FromInt64(30));
            loader.AppendProperty(new VertexId(1), keyName, PropertyValue.FromString("bob"));
            loader.Commit();
        }
        else
        {
            using var loader = db.BeginBulkLoad();
            for (int i = 0; i < 3; i++)
                loader.AppendVertex(new VertexId(i), new LabelId(0));
            loader.AppendEdge(new EdgeId(0), new VertexId(0), new VertexId(1), new EdgeTypeId(0));
            loader.AppendEdge(new EdgeId(1), new VertexId(1), new VertexId(2), new EdgeTypeId(0));
            loader.AppendProperty(new VertexId(0), keyName, PropertyValue.FromString("alice"));
            loader.AppendProperty(new VertexId(0), keyAge,  PropertyValue.FromInt64(30));
            loader.AppendProperty(new VertexId(1), keyName, PropertyValue.FromString("bob"));
            loader.Commit();
        }
    }

    // すべてのコアストアは単一ファイル graph.quiver に同居する。一括読み込みは決定的で、
    // WAL 非対象 (LSN=0) なので、streaming / in-memory の graph.quiver はバイト一致するはず。
    private static void AssertVertexStoreBytesEqual(string dirA, string dirB)
        => AssertFileBytesEqual(Path.Combine(dirA, "graph.quiver"), Path.Combine(dirB, "graph.quiver"));

    private static void AssertEdgeStoreBytesEqual(string dirA, string dirB)
        => AssertFileBytesEqual(Path.Combine(dirA, "graph.quiver"), Path.Combine(dirB, "graph.quiver"));

    private static void AssertPropStoreBytesEqual(string dirA, string dirB)
        => AssertFileBytesEqual(Path.Combine(dirA, "graph.quiver"), Path.Combine(dirB, "graph.quiver"));

    private static void AssertFileBytesEqual(string a, string b)
    {
        var ba = File.ReadAllBytes(a);
        var bb = File.ReadAllBytes(b);
        bb.Length.Should().Be(ba.Length, $"file lengths differ: {a} vs {b}");
        for (int i = 0; i < ba.Length; i++)
            if (ba[i] != bb[i])
            {
                false.Should().BeTrue(
                    $"file content differs at offset {i}: {a} has 0x{ba[i]:X2}, {b} has 0x{bb[i]:X2}");
                break;
            }
    }

    private static List<long> ExpandOut(IGraphTransaction tx, VertexId source)
    {
        var op = new ExpandOperator(
            new SingleVertexSource(source),
            sourceVertexColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborOnly);
        var result = tx.Execute(op);
        return result.Rows().Select(r => r.GetVertexId(0).Value).ToList();
    }

    private sealed class SingleVertexSource : IPhysicalOperator
    {
        private readonly TupleSlot[] _buf = new TupleSlot[1];
        private readonly VertexId _vertex;
        private bool _emitted;

        public SingleVertexSource(VertexId vertex) { _vertex = vertex; }
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.VertexId)]);
        public OperatorStatistics Statistics { get; private set; }
        public TupleRef Current => new(_buf);

        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }

        public bool MoveNext()
        {
            if (_emitted) return false;
            _buf[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _vertex.Value };
            _emitted = true;
            return true;
        }

        public void Dispose() { }
    }
}
