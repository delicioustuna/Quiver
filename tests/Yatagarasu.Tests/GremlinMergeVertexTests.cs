using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// MERGE / UPSERT を検証する。
/// <c>Yatagarasu.Backend.Tests</c> の契約テストに加え、
/// <c>GraphTraversalSource.MergeVertex</c> の簡略 API と
/// ON CREATE SET / ON MATCH SET の分岐パターンを対象とする。
/// </summary>
public sealed class GremlinMergeVertexTests : IDisposable
{
    private readonly string _dir;
    private readonly YatagarasuDatabase _db;

    public GremlinMergeVertexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_gc5_" + Guid.NewGuid().ToString("N"));
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void TraversalSource_MergeVertex_creates_when_missing()
    {
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;

        var (id, created) = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));

        created.Should().BeTrue();
        tx.VertexExists(id).Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void TraversalSource_MergeVertex_is_idempotent()
    {
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;

        var first  = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));
        var second = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));
        var third  = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));

        first.Created.Should().BeTrue();
        second.Created.Should().BeFalse();
        third.Created.Should().BeFalse();
        second.Id.Should().Be(first.Id);
        third.Id.Should().Be(first.Id);
        tx.Commit();
    }

    [Fact]
    public void MergeVertex_with_OnCreateSet_branches_only_on_create()
    {
        // Pattern: MERGE (n:Person {name:'Alice'}) ON CREATE SET n.createdAt=1
        //          ON MATCH SET n.seen = n.seen + 1
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;

        var (id, created) = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));
        if (created) tx.SetProperty(id, "createdAt", PropertyValue.FromInt64(1L));
        else         tx.SetProperty(id, "seen",      PropertyValue.FromInt64(1L));

        // Second pass: must hit the ON MATCH branch.
        var (id2, created2) = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));
        created2.Should().BeFalse();
        id2.Should().Be(id);
        if (created2) tx.SetProperty(id2, "createdAt", PropertyValue.FromInt64(99L));
        else          tx.SetProperty(id2, "seen",      PropertyValue.FromInt64(2L));

        tx.GetProperty(id, "createdAt").Int64Value.Should().Be(1L);
        tx.GetProperty(id, "seen").Int64Value.Should().Be(2L);
        tx.Commit();
    }

    [Fact]
    public void MergeVertex_match_property_is_visible_after_commit()
    {
        VertexId id;
        using (var tx = _db.BeginWriteTransaction())
        {
            var g = tx.Query;
            (id, _) = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Carol"));
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        System.Text.Encoding.UTF8.GetString(rtx.GetProperty(id, "name").Utf8StringValue)
            .Should().Be("Carol");
    }

    [Fact]
    public void MergeVertex_unminted_property_key_takes_create_path()
    {
        // A key the database has never seen cannot match anything, so the
        // create path must be taken without any wasted scan work.
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;

        var (_, created) = tx.Mutate.MergeVertex("Person", "totallyNewKey",
            PropertyValue.FromString("nope"));
        created.Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void MergeVertex_with_int_match_property_round_trips()
    {
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;

        var (a, ca) = tx.Mutate.MergeVertex("Item", "sku", PropertyValue.FromInt64(7L));
        var (b, cb) = tx.Mutate.MergeVertex("Item", "sku", PropertyValue.FromInt64(7L));
        var (c, cc) = tx.Mutate.MergeVertex("Item", "sku", PropertyValue.FromInt64(8L));

        ca.Should().BeTrue();
        cb.Should().BeFalse();
        cc.Should().BeTrue();
        b.Should().Be(a);
        c.Should().NotBe(a);
        tx.Commit();
    }

    [Fact]
    public void MergeVertex_does_not_match_vertex_with_same_label_but_different_value()
    {
        using var tx = _db.BeginWriteTransaction();
        var g = tx.Query;

        var (alice, _) = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));
        var (bob,   created) = tx.Mutate.MergeVertex("Person", "name", PropertyValue.FromString("Bob"));

        created.Should().BeTrue();
        bob.Should().NotBe(alice);
        tx.Commit();
    }
}
