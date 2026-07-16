using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// Edgeの upsert 操作 <c>MergeEdge</c> を検証する。
/// <c>MergeVertex</c> と対称な「既存があれば返す / 無ければ作る」セマンティクスと、
/// (source, target, type) の取り違えで別のEdgeが誤一致しないことを確認する。
/// </summary>
public sealed class MergeEdgeTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public MergeEdgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_ws1_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static (VertexId a, VertexId b) Pair(GraphTraversalSource g)
        => (g.AddVertex("Person").Next(), g.AddVertex("Tool").Next());

    [Fact]
    public void MergeEdge_creates_when_missing()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        var (a, b) = Pair(g);

        var (id, created) = g.MergeEdge(a, b, "USES");

        created.Should().BeTrue();
        id.IsValid.Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_is_idempotent_within_tx()
    {
        // read-your-writes: 同一 tx で直前に作成したエッジを 2 回目以降で検出する。
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        var (a, b) = Pair(g);

        var first  = g.MergeEdge(a, b, "USES");
        var second = g.MergeEdge(a, b, "USES");
        var third  = g.MergeEdge(a, b, "USES");

        first.Created.Should().BeTrue();
        second.Created.Should().BeFalse();
        third.Created.Should().BeFalse();
        second.Id.Should().Be(first.Id);
        third.Id.Should().Be(first.Id);
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_different_target_creates_new_edge()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        var a = g.AddVertex("Person").Next();
        var b = g.AddVertex("Tool").Next();
        var c = g.AddVertex("Tool").Next();

        var (e1, c1) = g.MergeEdge(a, b, "USES");
        var (e2, c2) = g.MergeEdge(a, c, "USES");

        c1.Should().BeTrue();
        c2.Should().BeTrue();
        e2.Should().NotBe(e1);
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_different_type_does_not_match()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        var (a, b) = Pair(g);

        var (uses, c1)  = g.MergeEdge(a, b, "USES");
        var (owns, c2)  = g.MergeEdge(a, b, "OWNS");   // 別型は誤マッチしない

        c1.Should().BeTrue();
        c2.Should().BeTrue();
        owns.Should().NotBe(uses);
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_unminted_type_takes_create_path()
    {
        // 一度も観測されていない型名はマッチし得ない → 走査せず作成パスへ。
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        var (a, b) = Pair(g);

        var (_, created) = g.MergeEdge(a, b, "BRAND_NEW_TYPE");
        created.Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_finds_existing_after_commit()
    {
        VertexId a, b;
        EdgeId created;
        using (var tx = _db.BeginTransaction())
        {
            var g = tx.G(_db.Schema);
            (a, b) = Pair(g);
            (created, _) = g.MergeEdge(a, b, "USES");
            tx.Commit();
        }

        using var tx2 = _db.BeginTransaction();
        var g2 = tx2.G(_db.Schema);
        var (id, isNew) = g2.MergeEdge(a, b, "USES");
        isNew.Should().BeFalse();
        id.Should().Be(created);
        tx2.Commit();
    }
}
