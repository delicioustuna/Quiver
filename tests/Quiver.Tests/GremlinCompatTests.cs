using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <see cref="GraphTraversal{T}"/> が提供する基本的な Gremlin / Cypher 互換 API を検証する。
/// 各ステップを個別のテストで扱い、回帰時の影響範囲を絞る。
/// </summary>
public sealed class GremlinCompatTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public GremlinCompatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_gc1_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private VertexId AddPerson(IWriteTransaction tx, string name, int? age = null)
    {
        var id = tx.CreateVertex("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString(name));
        if (age.HasValue) tx.SetProperty(id, "age", PropertyValue.FromInt64(age.Value));
        return id;
    }

    [Fact]
    public void Has_keeps_only_vertices_with_the_property()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var withAge = g.Vertices().HasLabel("Person").Has("age").ToList();
        withAge.Should().HaveCount(1);
    }

    [Fact]
    public void HasNot_keeps_only_vertices_missing_the_property()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            AddPerson(tx, "Carol"); // no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var ageless = g.Vertices().HasLabel("Person").HasNot("age").ToList();
        ageless.Should().HaveCount(2);
    }

    [Fact]
    public void Limit_Skip_Range_paginate_correctly()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 10; i++) AddPerson(tx, $"P{i}");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Vertices().HasLabel("Person").Limit(3).ToList().Should().HaveCount(3);
        g.Vertices().HasLabel("Person").Skip(7).ToList().Should().HaveCount(3);
        g.Vertices().HasLabel("Person").Range(2, 5).ToList().Should().HaveCount(3);
    }

    [Fact]
    public void HasNext_is_true_iff_at_least_one_result()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Vertices().HasLabel("Person").HasNext().Should().BeTrue();
        g.Vertices().HasLabel("Missing").HasNext().Should().BeFalse();
    }

    [Fact]
    public void Label_projects_the_label_name()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            tx.CreateVertex("Person");
            tx.CreateVertex("Company");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var labels = g.Vertices().Label().ToList();
        labels.Should().Contain("Person").And.Contain("Company");
    }

    [Fact]
    public void Id_projects_the_raw_long_id()
    {
        long firstId;
        using (var tx = _db.BeginWriteTransaction())
        {
            var n = AddPerson(tx, "Alice");
            firstId = n.Value;
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var ids = g.Vertices().HasLabel("Person").Id().ToList();
        ids.Should().Contain(firstId).And.HaveCount(2);
    }

    [Fact]
    public void OutV_and_InV_resolve_edge_endpoints()
    {
        VertexId alice, bob;
        using (var tx = _db.BeginWriteTransaction())
        {
            alice = AddPerson(tx, "Alice");
            bob = AddPerson(tx, "Bob");
            tx.CreateEdge(alice, bob, "KNOWS");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var sources = g.Vertex(alice).OutEdges("KNOWS").SourceVertex().ToList();
        var targets = g.Vertex(alice).OutEdges("KNOWS").TargetVertex().ToList();

        sources.Should().ContainSingle().Which.Value.Should().Be(alice.Value);
        targets.Should().ContainSingle().Which.Value.Should().Be(bob.Value);
    }

    [Fact]
    public void Has_with_P_Without_excludes_listed_values()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice");
            AddPerson(tx, "Bob");
            AddPerson(tx, "Carol");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var notAlice = g.Vertices().HasLabel("Person")
            .Has("name", P.Without("Alice", "Bob"))
            .ToList();
        notAlice.Should().HaveCount(1);
    }

    [Fact]
    public void Values_Is_filters_by_property_equality()
    {
        VertexId alice;
        using (var tx = _db.BeginWriteTransaction())
        {
            alice = AddPerson(tx, "Alice");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var aliceNames = g.Vertices().HasLabel("Person").Values("name").Is("Alice").ToList();
        aliceNames.Should().ContainSingle().Which.Should().Be("Alice");
    }
}
