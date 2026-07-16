using FluentAssertions;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// SetProperty / DeleteVertex の書き込み経路に統合された全文インデックス保守を検証する。
/// 挿入、更新時の更新前イメージ削除、削除、ロールバックのすべてで、
/// 同一トランザクション内の Postings と Norms が整合することを確認する。
/// </summary>
public sealed class FullTextMaintenanceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public FullTextMaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts2_maint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static FullTextIndex Ft(QuiverDatabase db, string name)
    {
        ((SchemaApi)db.Schema).IndexManager.TryGetFullTextIndex(name, out var ft).Should().BeTrue();
        return ft;
    }

    [Fact]
    public void SetProperty_on_indexed_label_writes_postings()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");

        VertexId vertex;
        using (var tx = db.BeginTransaction())
        {
            vertex = tx.CreateVertex("Doc");
            tx.SetProperty(vertex, "body", PropertyValue.FromString("hello world"));
            tx.Commit();
        }

        var ft = Ft(db, "idx_body");
        ft.DocumentCount.Should().Be(1);
        var hello = ft.GetPostings("hello");
        hello.Should().ContainSingle();
        EntityRef.UnpackSequence(hello[0].EntityId).Should().Be(vertex.Sequence);
    }

    [Fact]
    public void Read_your_own_writes_within_the_same_transaction()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");
        var ft = Ft(db, "idx_body");

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString("inflight content"));
        // Postings are visible before commit (same-Tx read-your-own-writes).
        ft.GetPostings("inflight").Should().ContainSingle();
        tx.Commit();
    }

    [Fact]
    public void Updating_property_removes_old_terms_via_before_image()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");

        VertexId vertex;
        using (var tx = db.BeginTransaction())
        {
            vertex = tx.CreateVertex("Doc");
            tx.SetProperty(vertex, "body", PropertyValue.FromString("hello world"));
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.SetProperty(vertex, "body", PropertyValue.FromString("goodbye world"));
            tx.Commit();
        }

        var ft = Ft(db, "idx_body");
        ft.GetPostings("hello").Should().BeEmpty();
        ft.GetPostings("goodbye").Should().ContainSingle();
        ft.GetPostings("world").Should().ContainSingle();
        ft.DocumentCount.Should().Be(1); // norm replaced, not duplicated
    }

    [Fact]
    public void Rollback_leaves_no_postings()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");

        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateVertex("Doc");
            tx.SetProperty(n, "body", PropertyValue.FromString("transient text"));
            tx.Rollback();
        }

        var ft = Ft(db, "idx_body");
        ft.GetPostings("transient").Should().BeEmpty();
        ft.DocumentCount.Should().Be(0);
    }

    [Fact]
    public void DeleteVertex_removes_postings()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");

        VertexId vertex;
        using (var tx = db.BeginTransaction())
        {
            vertex = tx.CreateVertex("Doc");
            tx.SetProperty(vertex, "body", PropertyValue.FromString("hello world"));
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteVertex(vertex);
            tx.Commit();
        }

        var ft = Ft(db, "idx_body");
        ft.GetPostings("hello").Should().BeEmpty();
        ft.DocumentCount.Should().Be(0);
    }

    [Fact]
    public void Writes_to_unbound_keys_are_not_indexed()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");

        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateVertex("Doc");
            tx.SetProperty(n, "title", PropertyValue.FromString("not indexed")); // 'title' is unbound
            tx.Commit();
        }

        var ft = Ft(db, "idx_body");
        ft.GetPostings("not").Should().BeEmpty();
        ft.DocumentCount.Should().Be(0);
    }
}
