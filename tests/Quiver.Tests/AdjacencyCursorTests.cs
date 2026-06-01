using Quiver;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// PW-8: high-degree adjacency cursor must walk the full block chain without
/// truncating. The contiguous block layout packs ~581 entries per page, so
/// degrees well above one page exercise the multi-page continuation path.
/// </summary>
public sealed class AdjacencyCursorTests : IDisposable
{
    private readonly string _dir;
    private GraphDatabase? _db;

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
        _db = GraphDatabase.Open(_dir);

        using var tx = _db.BeginTransaction();
        var adj = tx.AsInternal().AdjacencyBlocks!;
        adj.HasBlock(new NodeId(0)).Should().BeTrue();

        var seen = new HashSet<long>();
        using var cursor = adj.OpenCursor(new NodeId(0), Direction.Outgoing, null);
        while (cursor.MoveNext())
            seen.Add(cursor.Neighbor.Value).Should().BeTrue("each neighbor is unique");

        seen.Should().HaveCount(degree);
        for (long i = 1; i <= degree; i++)
            seen.Should().Contain(i);
    }

    [Fact]
    public void OpenCursor_honors_type_filter_across_pages()
    {
        // Build a hub with two relationship types; cursor with type filter must
        // skip the non-matching ones across multiple pages.
        const int half = 1_000;
        {
            using var db = GraphDatabase.Open(_dir);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.AppendNode(new NodeId(0), new LabelId(0));
            long relId = 0;
            for (int i = 1; i <= 2 * half; i++)
            {
                loader.AppendNode(new NodeId(i), new LabelId(1));
                // Even i → type 0 (matches filter), odd i → type 1 (skipped).
                short type = (short)(i % 2 == 0 ? 0 : 1);
                loader.AppendRelationship(new RelationshipId(relId++),
                    new NodeId(0), new NodeId(i), new RelationshipTypeId(type));
            }
            loader.Commit();
        }
        _db = GraphDatabase.Open(_dir);

        using var tx = _db.BeginTransaction();
        var adj = tx.AsInternal().AdjacencyBlocks!;
        int count = 0;
        using var cursor = adj.OpenCursor(new NodeId(0), Direction.Outgoing, new RelationshipTypeId(0));
        while (cursor.MoveNext())
        {
            cursor.Type.Should().Be(new RelationshipTypeId(0));
            // Only even neighbour ids are emitted.
            (cursor.Neighbor.Value % 2).Should().Be(0);
            count++;
        }
        count.Should().Be(half);
    }

    [Fact]
    public void OpenCursor_returns_empty_when_node_has_no_block()
    {
        BulkLoadHub(10);
        _db = GraphDatabase.Open(_dir);

        using var tx = _db.BeginTransaction();
        var adj = tx.AsInternal().AdjacencyBlocks!;
        // Node id beyond the high watermark has no block.
        adj.HasBlock(new NodeId(99_999)).Should().BeFalse();
        using var cursor = adj.OpenCursor(new NodeId(99_999), Direction.Outgoing, null);
        cursor.MoveNext().Should().BeFalse();
    }

    private void BulkLoadHub(int degree)
    {
        using var db = GraphDatabase.Open(_dir);
        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        loader.AppendNode(new NodeId(0), new LabelId(0));
        for (int i = 1; i <= degree; i++)
        {
            loader.AppendNode(new NodeId(i), new LabelId(1));
            loader.AppendRelationship(new RelationshipId(i - 1),
                new NodeId(0), new NodeId(i), new RelationshipTypeId(0));
        }
        loader.Commit();
    }
}
