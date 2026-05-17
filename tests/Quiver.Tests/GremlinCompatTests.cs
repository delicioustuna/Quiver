using FluentAssertions;
using Quiver.Client;
using Quiver.Core;
using Quiver.Stores;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// GC-1 coverage: low-difficulty Gremlin/Cypher API surface on
/// <see cref="GraphTraversal{T}"/>. Each new step gets a focused test so the
/// regression surface stays narrow.
/// </summary>
public sealed class GremlinCompatTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public GremlinCompatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_gc1_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(_dir);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private NodeId AddPerson(IGraphTransaction tx, string name, int? age = null)
    {
        var id = tx.CreateNode("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString(name));
        if (age.HasValue) tx.SetProperty(id, "age", PropertyValue.FromInt64(age.Value));
        return id;
    }

    [Fact]
    public void Has_keeps_only_nodes_with_the_property()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var withAge = g.Nodes().HasLabel("Person").Has("age").ToList();
        withAge.Should().HaveCount(1);
    }

    [Fact]
    public void HasNot_keeps_only_nodes_missing_the_property()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            AddPerson(tx, "Carol"); // no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var ageless = g.Nodes().HasLabel("Person").HasNot("age").ToList();
        ageless.Should().HaveCount(2);
    }

    [Fact]
    public void Limit_Skip_Range_paginate_correctly()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 10; i++) AddPerson(tx, $"P{i}");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Nodes().HasLabel("Person").Limit(3).ToList().Should().HaveCount(3);
        g.Nodes().HasLabel("Person").Skip(7).ToList().Should().HaveCount(3);
        g.Nodes().HasLabel("Person").Range(2, 5).ToList().Should().HaveCount(3);
    }

    [Fact]
    public void HasNext_is_true_iff_at_least_one_result()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Nodes().HasLabel("Person").HasNext().Should().BeTrue();
        g.Nodes().HasLabel("Missing").HasNext().Should().BeFalse();
    }

    [Fact]
    public void Label_projects_the_label_name()
    {
        using (var tx = _db.BeginTransaction())
        {
            tx.CreateNode("Person");
            tx.CreateNode("Company");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var labels = g.Nodes().Label().ToList();
        labels.Should().Contain("Person").And.Contain("Company");
    }

    [Fact]
    public void Id_projects_the_raw_long_id()
    {
        long firstId;
        using (var tx = _db.BeginTransaction())
        {
            var n = AddPerson(tx, "Alice");
            firstId = n.Value;
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var ids = g.Nodes().HasLabel("Person").Id().ToList();
        ids.Should().Contain(firstId).And.HaveCount(2);
    }

    [Fact]
    public void OutV_and_InV_resolve_relationship_endpoints()
    {
        NodeId alice, bob;
        using (var tx = _db.BeginTransaction())
        {
            alice = AddPerson(tx, "Alice");
            bob = AddPerson(tx, "Bob");
            tx.CreateRelationship(alice, bob, "KNOWS");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var sources = g.Node(alice).OutRelationships("KNOWS").SourceNode().ToList();
        var targets = g.Node(alice).OutRelationships("KNOWS").TargetNode().ToList();

        sources.Should().ContainSingle().Which.Value.Should().Be(alice.Value);
        targets.Should().ContainSingle().Which.Value.Should().Be(bob.Value);
    }

    [Fact]
    public void Has_with_P_Without_excludes_listed_values()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice");
            AddPerson(tx, "Bob");
            AddPerson(tx, "Carol");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var notAlice = g.Nodes().HasLabel("Person")
            .Has("name", P.Without("Alice", "Bob"))
            .ToList();
        notAlice.Should().HaveCount(1);
    }

    [Fact]
    public void Values_Is_filters_by_property_equality()
    {
        NodeId alice;
        using (var tx = _db.BeginTransaction())
        {
            alice = AddPerson(tx, "Alice");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var aliceNames = g.Nodes().HasLabel("Person").Values("name").Is("Alice").ToList();
        aliceNames.Should().ContainSingle().Which.Should().Be("Alice");
    }
}
