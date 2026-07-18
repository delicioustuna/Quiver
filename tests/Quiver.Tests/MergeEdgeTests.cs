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

    private static (VertexId a, VertexId b) Pair(IWriteTransaction tx)
        => (tx.Mutate.AddVertex("Person").Next(), tx.Mutate.AddVertex("Tool").Next());

    [Fact]
    public void MergeEdge_creates_when_missing()
    {
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;
        var (a, b) = Pair(tx);

        var (id, created) = tx.Mutate.MergeEdge(a, b, "USES");

        created.Should().BeTrue();
        id.IsValid.Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_is_idempotent_within_tx()
    {
        // read-your-writes: 同一 tx で直前に作成したエッジを 2 回目以降で検出する。
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;
        var (a, b) = Pair(tx);

        var first  = tx.Mutate.MergeEdge(a, b, "USES");
        var second = tx.Mutate.MergeEdge(a, b, "USES");
        var third  = tx.Mutate.MergeEdge(a, b, "USES");

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
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;
        var a = tx.Mutate.AddVertex("Person").Next();
        var b = tx.Mutate.AddVertex("Tool").Next();
        var c = tx.Mutate.AddVertex("Tool").Next();

        var (e1, c1) = tx.Mutate.MergeEdge(a, b, "USES");
        var (e2, c2) = tx.Mutate.MergeEdge(a, c, "USES");

        c1.Should().BeTrue();
        c2.Should().BeTrue();
        e2.Should().NotBe(e1);
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_different_type_does_not_match()
    {
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;
        var (a, b) = Pair(tx);

        var (uses, c1)  = tx.Mutate.MergeEdge(a, b, "USES");
        var (owns, c2)  = tx.Mutate.MergeEdge(a, b, "OWNS");   // 別型は誤マッチしない

        c1.Should().BeTrue();
        c2.Should().BeTrue();
        owns.Should().NotBe(uses);
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_unminted_type_takes_create_path()
    {
        // 一度も観測されていない型名はマッチし得ない → 走査せず作成パスへ。
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;
        var (a, b) = Pair(tx);

        var (_, created) = tx.Mutate.MergeEdge(a, b, "BRAND_NEW_TYPE");
        created.Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_finds_existing_after_commit()
    {
        VertexId a, b;
        EdgeId created;
        using (var tx = _db.BeginWriteTransaction())
        {
            var g = tx.Query;
            (a, b) = Pair(tx);
            (created, _) = tx.Mutate.MergeEdge(a, b, "USES");
            tx.Commit();
        }

        using var tx2 = _db.BeginWriteTransaction();
        var g2 = tx2.Query;
        var (id, isNew) = tx2.Mutate.MergeEdge(a, b, "USES");
        isNew.Should().BeFalse();
        id.Should().Be(created);
        tx2.Commit();
    }
}
