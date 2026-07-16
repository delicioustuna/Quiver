using Quiver;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 高次数Vertexの隣接カーソルがブロックチェーンを途中で切らずに走査することを検証する。
/// 連続ブロック形式は 1 ページに約 581 エントリを格納するため、
/// それを十分に超える次数で複数ページの継続処理を通す。
/// </summary>
public sealed class AdjacencyCursorTests : IDisposable
{
    private readonly string _dir;
    private QuiverDatabase? _db;

    public AdjacencyCursorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_adj_cursor_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(5)]      // single page
    [InlineData(600)]    // just over one page (~581 per page)
    [InlineData(5_000)]  // many pages
    [InlineData(10_000)] // 完了条件 (高次数 + linked-list fallback 不要)
    public void OpenCursor_returns_all_neighbors_regardless_of_degree(int degree)
    {
        BulkLoadHub(degree);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        var adj = tx.AsInternal().AdjacencyBlocks!;
        adj.HasBlock(new VertexId(0)).Should().BeTrue();

        var seen = new HashSet<long>();
        using var cursor = adj.OpenCursor(new VertexId(0), Direction.Outgoing, null);
        while (cursor.MoveNext())
            seen.Add(cursor.Neighbor.Value).Should().BeTrue("each neighbor is unique");

        seen.Should().HaveCount(degree);
        for (long i = 1; i <= degree; i++)
            seen.Should().Contain(i);
    }

    [Fact]
    public void OpenCursor_honors_type_filter_across_pages()
    {
        // 2 種類のEdgeを持つハブを作り、
        // 型フィルター付きカーソルが複数ページにまたがって不一致を除外することを確認する。
        const int half = 1_000;
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.AppendVertex(new VertexId(0), new LabelId(0));
            long edgeId = 0;
            for (int i = 1; i <= 2 * half; i++)
            {
                loader.AppendVertex(new VertexId(i), new LabelId(1));
                // 偶数は型 0 で一致し、奇数は型 1 なので除外される。
                short type = (short)(i % 2 == 0 ? 0 : 1);
                loader.AppendEdge(new EdgeId(edgeId++),
                    new VertexId(0), new VertexId(i), new EdgeTypeId(type));
            }
            loader.Commit();
        }
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        var adj = tx.AsInternal().AdjacencyBlocks!;
        int count = 0;
        using var cursor = adj.OpenCursor(new VertexId(0), Direction.Outgoing, new EdgeTypeId(0));
        while (cursor.MoveNext())
        {
            cursor.Type.Should().Be(new EdgeTypeId(0));
            // 偶数番目の隣接Vertexだけが出力される。
            (cursor.Neighbor.Value % 2).Should().Be(0);
            count++;
        }
        count.Should().Be(half);
    }

    [Fact]
    public void OpenCursor_returns_empty_when_vertex_has_no_block()
    {
        BulkLoadHub(10);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        var adj = tx.AsInternal().AdjacencyBlocks!;
        // 高水位標を超えるVertex ID にはブロックがない。
        adj.HasBlock(new VertexId(99_999)).Should().BeFalse();
        using var cursor = adj.OpenCursor(new VertexId(99_999), Direction.Outgoing, null);
        cursor.MoveNext().Should().BeFalse();
    }

    private void BulkLoadHub(int degree)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        loader.AppendVertex(new VertexId(0), new LabelId(0));
        for (int i = 1; i <= degree; i++)
        {
            loader.AppendVertex(new VertexId(i), new LabelId(1));
            loader.AppendEdge(new EdgeId(i - 1),
                new VertexId(0), new VertexId(i), new EdgeTypeId(0));
        }
        loader.Commit();
    }
}
