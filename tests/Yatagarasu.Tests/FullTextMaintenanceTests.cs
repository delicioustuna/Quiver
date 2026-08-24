using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// property mutation がimmutable全文segmentへ反映されることを検証する。
/// </summary>
public sealed class FullTextMaintenanceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public FullTextMaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_fts2_maint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.yata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static List<VertexId> Search(
        IReadTransaction transaction,
        string query)
    {
        return transaction.Query.Search("idx_body", query, 10).ToList();
    }

    [Fact]
    public void Committed_text_is_searchable()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition("idx_body", new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));

        VertexId vertex;
        using (var tx = db.BeginWriteTransaction())
        {
            vertex = tx.CreateVertex("Doc");
            tx.SetProperty(vertex, "body", PropertyValue.FromString("hello world"));
            tx.Commit();
        }

        using var read = db.BeginReadTransaction();
        Search(read, "hello").Should().ContainSingle().Which.Should().Be(vertex);
    }

    [Fact]
    public void Read_your_own_writes_within_the_same_transaction()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition("idx_body", new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
        using var tx = db.BeginWriteTransaction();
        var n = tx.CreateVertex("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString("inflight content"));
        Search(tx, "inflight").Should().ContainSingle().Which.Should().Be(n);
        tx.Commit();
    }

    [Fact]
    public void Updating_text_replaces_visible_terms()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition("idx_body", new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));

        VertexId vertex;
        using (var tx = db.BeginWriteTransaction())
        {
            vertex = tx.CreateVertex("Doc");
            tx.SetProperty(vertex, "body", PropertyValue.FromString("hello world"));
            tx.Commit();
        }
        using (var tx = db.BeginWriteTransaction())
        {
            tx.SetProperty(vertex, "body", PropertyValue.FromString("goodbye world"));
            tx.Commit();
        }

        using var read = db.BeginReadTransaction();
        Search(read, "hello").Should().BeEmpty();
        Search(read, "goodbye").Should().ContainSingle().Which.Should().Be(vertex);
        Search(read, "world").Should().ContainSingle().Which.Should().Be(vertex);
    }

    [Fact]
    public void Rollback_leaves_no_postings()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition("idx_body", new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));

        using (var tx = db.BeginWriteTransaction())
        {
            var n = tx.CreateVertex("Doc");
            tx.SetProperty(n, "body", PropertyValue.FromString("transient text"));
            tx.Rollback();
        }

        using var read = db.BeginReadTransaction();
        Search(read, "transient").Should().BeEmpty();
    }

    [Fact]
    public void DeleteVertex_removes_postings()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition("idx_body", new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));

        VertexId vertex;
        using (var tx = db.BeginWriteTransaction())
        {
            vertex = tx.CreateVertex("Doc");
            tx.SetProperty(vertex, "body", PropertyValue.FromString("hello world"));
            tx.Commit();
        }
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(vertex);
            tx.Commit();
        }

        using var read = db.BeginReadTransaction();
        Search(read, "hello").Should().BeEmpty();
    }

    [Fact]
    public void Writes_to_unbound_keys_are_not_indexed()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition("idx_body", new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));

        using (var tx = db.BeginWriteTransaction())
        {
            var n = tx.CreateVertex("Doc");
            tx.SetProperty(n, "title", PropertyValue.FromString("not indexed")); // 'title' is unbound
            tx.Commit();
        }

        using var read = db.BeginReadTransaction();
        Search(read, "not").Should().BeEmpty();
    }
}
