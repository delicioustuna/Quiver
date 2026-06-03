using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// GC-2 coverage: extended predicates (STARTS WITH / ENDS WITH / CONTAINS /
/// regex), predicate-level boolean composition (P.Not / P.And / P.Or),
/// traversal-level And/Or sub-queries, and Cypher IS NULL / IS NOT NULL aliases.
/// </summary>
public sealed class GremlinCompatGc2Tests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public GremlinCompatGc2Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_gc2_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
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
    public void StartsWith_keeps_strings_with_matching_prefix()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice");
            AddPerson(tx, "Alex");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var aPrefix = g.Nodes().HasLabel("Person").Has("name", P.StartsWith("Al")).ToList();
        aPrefix.Should().HaveCount(2);
    }

    [Fact]
    public void EndsWith_keeps_strings_with_matching_suffix()
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

        var iceSuffix = g.Nodes().HasLabel("Person").Has("name", P.EndsWith("ice")).ToList();
        iceSuffix.Should().ContainSingle();
    }

    [Fact]
    public void Contains_keeps_strings_with_matching_substring()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice");
            AddPerson(tx, "Malice");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var li = g.Nodes().HasLabel("Person").Has("name", P.Contains("li")).ToList();
        li.Should().HaveCount(2);
    }

    [Fact]
    public void Regex_keeps_strings_that_match_pattern()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice");
            AddPerson(tx, "Alex");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var aStar = g.Nodes().HasLabel("Person").Has("name", P.Regex("^Al.*")).ToList();
        aStar.Should().HaveCount(2);
    }

    [Fact]
    public void Not_inverts_inner_predicate()
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

        var notAlice = g.Nodes().HasLabel("Person").Has("name", P.Not(P.Eq("Alice"))).ToList();
        notAlice.Should().HaveCount(2);
    }

    [Fact]
    public void Not_returns_true_when_property_missing()
    {
        // Cypher: WHERE NOT n.age = 30 evaluates to NOT false = true for nodes
        // that don't have an `age` property at all. The wrapped predicate
        // returns false when the key is absent, so NOT yields true.
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var notThirty = g.Nodes().HasLabel("Person").Has("age", P.Not(P.Eq(30))).ToList();
        notThirty.Should().HaveCount(1);
    }

    [Fact]
    public void And_compound_predicate_requires_all_inner_predicates()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 25);
            AddPerson(tx, "Adam",  age: 50);
            AddPerson(tx, "Bob",   age: 30);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // half-open range [20, 40) on age
        var inRange = g.Nodes().HasLabel("Person").Has("age", P.And(P.Gte(20), P.Lt(40))).ToList();
        inRange.Should().HaveCount(2);
    }

    [Fact]
    public void Or_compound_predicate_passes_if_any_inner_predicate_passes()
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

        var alOrBo = g.Nodes().HasLabel("Person")
            .Has("name", P.Or(P.StartsWith("Al"), P.StartsWith("Bo")))
            .ToList();
        alOrBo.Should().HaveCount(2);
    }

    [Fact]
    public void Traversal_Or_keeps_elements_passing_any_sub_traversal()
    {
        NodeId alice, bob, carol;
        using (var tx = _db.BeginTransaction())
        {
            alice = AddPerson(tx, "Alice");
            bob = AddPerson(tx, "Bob");
            carol = AddPerson(tx, "Carol");
            tx.CreateRelationship(alice, bob, "KNOWS");
            tx.SetProperty(carol, "title", PropertyValue.FromString("VIP"));
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // matches Alice (KNOWS edge) and Carol (title=VIP); Bob has neither
        var either = g.Nodes().HasLabel("Person")
            .Or(t => t.Out("KNOWS"),
               t => t.Has("title", "VIP"))
            .ToList();
        either.Should().HaveCount(2);
        either.Should().Contain(alice).And.Contain(carol);
    }

    [Fact]
    public void Traversal_And_requires_every_sub_traversal_to_match()
    {
        NodeId alice, bob;
        using (var tx = _db.BeginTransaction())
        {
            alice = AddPerson(tx, "Alice", age: 30);
            bob = AddPerson(tx, "Bob");
            tx.CreateRelationship(alice, bob, "KNOWS");
            // Bob has no outgoing KNOWS edge and no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Only Alice has both an outgoing KNOWS edge AND an age property.
        var both = g.Nodes().HasLabel("Person")
            .And(t => t.Out("KNOWS"),
                 t => t.Has("age", P.Gte(0)))
            .ToList();
        both.Should().ContainSingle(n => n.Value == alice.Value);
    }

    [Fact]
    public void IsNull_and_IsNotNull_match_HasNot_and_Has()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Nodes().HasLabel("Person").IsNull("age").ToList().Should().HaveCount(1);
        g.Nodes().HasLabel("Person").IsNotNull("age").ToList().Should().HaveCount(1);
    }
}
