using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// GC-3 coverage: numeric aggregation (Sum/Max/Min/Mean), blocking sort
/// (Order / OrderBy / OrderByDescending), grouping (GroupCount), and Fold.
/// </summary>
public sealed class GremlinCompatGc3Tests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public GremlinCompatGc3Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_gc3_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(_dir);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private NodeId AddPerson(IGraphTransaction tx, string name, int? age = null, double? height = null, string? city = null)
    {
        var id = tx.CreateNode("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString(name));
        if (age.HasValue) tx.SetProperty(id, "age", PropertyValue.FromInt64(age.Value));
        if (height.HasValue) tx.SetProperty(id, "height", PropertyValue.FromDouble(height.Value));
        if (city != null) tx.SetProperty(id, "city", PropertyValue.FromString(city));
        return id;
    }

    [Fact]
    public void Sum_aggregates_int_property_values()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob",   age: 25);
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Nodes().HasLabel("Person").Sum("age").Should().Be(95);
        g.Nodes().HasLabel("Person").SumLong("age").Should().Be(95);
    }

    [Fact]
    public void Sum_skips_missing_and_non_numeric_values()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Nodes().HasLabel("Person").Sum("age").Should().Be(70);
    }

    [Fact]
    public void Sum_aggregates_double_property_values()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", height: 1.7);
            AddPerson(tx, "Bob",   height: 1.8);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Nodes().HasLabel("Person").Sum("height").Should().BeApproximately(3.5, 1e-9);
    }

    [Fact]
    public void Max_Min_Mean_return_expected_values()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob",   age: 25);
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Nodes().HasLabel("Person").Max("age").Should().Be(40);
        g.Nodes().HasLabel("Person").Min("age").Should().Be(25);
        g.Nodes().HasLabel("Person").Mean("age").Should().BeApproximately(95.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Aggregations_return_null_or_zero_for_empty_input()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Bob"); // no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        g.Nodes().HasLabel("Person").Sum("age").Should().Be(0.0);
        g.Nodes().HasLabel("Person").Max("age").Should().BeNull();
        g.Nodes().HasLabel("Person").Min("age").Should().BeNull();
        g.Nodes().HasLabel("Person").Mean("age").Should().BeNull();
    }

    [Fact]
    public void OrderBy_sorts_ascending()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob",   age: 25);
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Sort by age ascending, then read back the names in that order.
        var names = g.Nodes().HasLabel("Person").OrderBy("age").Values("name").ToList();
        names.Should().Equal("Bob", "Alice", "Carol");
    }

    [Fact]
    public void OrderByDescending_sorts_descending()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob",   age: 25);
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var names = g.Nodes().HasLabel("Person").OrderByDescending("age").Values("name").ToList();
        names.Should().Equal("Carol", "Alice", "Bob");
    }

    [Fact]
    public void OrderBy_followed_by_Limit_returns_top_N()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 10; i++) AddPerson(tx, $"P{i}", age: i * 10);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Top-3 oldest: ages 90, 80, 70 -> P9, P8, P7
        var top3 = g.Nodes().HasLabel("Person").OrderByDescending("age").Limit(3).Values("name").ToList();
        top3.Should().Equal("P9", "P8", "P7");
    }

    [Fact]
    public void OrderBy_string_property_uses_ordinal_compare()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Charlie");
            AddPerson(tx, "Alice");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var names = g.Nodes().HasLabel("Person").OrderBy("name").Values("name").ToList();
        names.Should().Equal("Alice", "Bob", "Charlie");
    }

    [Fact]
    public void OrderBy_pushes_missing_keys_to_the_end()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            AddPerson(tx, "Carol", age: 25);
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var names = g.Nodes().HasLabel("Person").OrderBy("age").Values("name").ToList();
        // Null slots are compared after non-null regardless of direction.
        names.Should().Equal("Carol", "Alice", "Bob");
    }

    [Fact]
    public void Order_by_entity_id_sorts_ascending()
    {
        NodeId first, second;
        using (var tx = _db.BeginTransaction())
        {
            first  = AddPerson(tx, "Alice");
            second = AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var ids = g.Nodes().HasLabel("Person").Order().Id().ToList();
        ids.Should().Equal(first.Value, second.Value);
    }

    [Fact]
    public void GroupCount_counts_by_string_property()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice", city: "Tokyo");
            AddPerson(tx, "Bob",   city: "Tokyo");
            AddPerson(tx, "Carol", city: "Osaka");
            AddPerson(tx, "Dan"); // no city
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var counts = g.Nodes().HasLabel("Person").GroupCount("city");
        counts["Tokyo"].Should().Be(2);
        counts["Osaka"].Should().Be(1);
        counts.Should().NotContainKey("");
        counts.Sum(kv => kv.Value).Should().Be(3);
    }

    [Fact]
    public void Fold_returns_the_same_list_as_ToList()
    {
        using (var tx = _db.BeginTransaction())
        {
            AddPerson(tx, "Alice");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var asList = g.Nodes().HasLabel("Person").Values("name").ToList();
        var asFold = g.Nodes().HasLabel("Person").Values("name").Fold();
        asFold.Should().Equal(asList);
    }
}
