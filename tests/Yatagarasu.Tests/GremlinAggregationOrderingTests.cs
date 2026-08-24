using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// 数値集約 (Sum、Max、Min、Mean)、ブロッキングソート
/// (Order、OrderBy、OrderByDescending)、グループ化 (GroupCount)、Fold を検証する。
/// </summary>
public sealed class GremlinAggregationOrderingTests : IDisposable
{
    private readonly string _dir;
    private readonly YatagarasuDatabase _db;

    public GremlinAggregationOrderingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_gc3_" + Guid.NewGuid().ToString("N"));
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private VertexId AddPerson(IWriteTransaction tx, string name, int? age = null, double? height = null, string? city = null)
    {
        var id = tx.CreateVertex("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString(name));
        if (age.HasValue) tx.SetProperty(id, "age", PropertyValue.FromInt64(age.Value));
        if (height.HasValue) tx.SetProperty(id, "height", PropertyValue.FromDouble(height.Value));
        if (city != null) tx.SetProperty(id, "city", PropertyValue.FromString(city));
        return id;
    }

    [Fact]
    public void Sum_aggregates_int_property_values()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob",   age: 25);
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Vertices().HasLabel("Person").Sum("age").Should().Be(95);
        g.Vertices().HasLabel("Person").SumLong("age").Should().Be(95);
    }

    [Fact]
    public void Sum_skips_missing_and_non_numeric_values()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Vertices().HasLabel("Person").Sum("age").Should().Be(70);
    }

    [Fact]
    public void Sum_aggregates_double_property_values()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", height: 1.7);
            AddPerson(tx, "Bob",   height: 1.8);
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Vertices().HasLabel("Person").Sum("height").Should().BeApproximately(3.5, 1e-9);
    }

    [Fact]
    public void Max_Min_Mean_return_expected_values()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob",   age: 25);
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Vertices().HasLabel("Person").Max("age").Should().Be(40);
        g.Vertices().HasLabel("Person").Min("age").Should().Be(25);
        g.Vertices().HasLabel("Person").Mean("age").Should().BeApproximately(95.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Aggregations_return_null_or_zero_for_empty_input()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Bob"); // no age
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        g.Vertices().HasLabel("Person").Sum("age").Should().Be(0.0);
        g.Vertices().HasLabel("Person").Max("age").Should().BeNull();
        g.Vertices().HasLabel("Person").Min("age").Should().BeNull();
        g.Vertices().HasLabel("Person").Mean("age").Should().BeNull();
    }

    [Fact]
    public void OrderBy_sorts_ascending()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob",   age: 25);
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // Sort by age ascending, then read back the names in that order.
        var names = g.Vertices().HasLabel("Person").OrderBy("age").Values("name").ToList();
        names.Should().Equal("Bob", "Alice", "Carol");
    }

    [Fact]
    public void OrderByDescending_sorts_descending()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob",   age: 25);
            AddPerson(tx, "Carol", age: 40);
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var names = g.Vertices().HasLabel("Person").OrderByDescending("age").Values("name").ToList();
        names.Should().Equal("Carol", "Alice", "Bob");
    }

    [Fact]
    public void OrderBy_followed_by_Limit_returns_top_N()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 10; i++) AddPerson(tx, $"P{i}", age: i * 10);
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // 年齢の高い上位 3 件は 90、80、70 の P9、P8、P7。
        var top3 = g.Vertices().HasLabel("Person").OrderByDescending("age").Limit(3).Values("name").ToList();
        top3.Should().Equal("P9", "P8", "P7");
    }

    [Fact]
    public void OrderBy_string_property_uses_ordinal_compare()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Charlie");
            AddPerson(tx, "Alice");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var names = g.Vertices().HasLabel("Person").OrderBy("name").Values("name").ToList();
        names.Should().Equal("Alice", "Bob", "Charlie");
    }

    [Fact]
    public void OrderBy_pushes_missing_keys_to_the_end()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", age: 30);
            AddPerson(tx, "Bob"); // no age
            AddPerson(tx, "Carol", age: 25);
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var names = g.Vertices().HasLabel("Person").OrderBy("age").Values("name").ToList();
        // Null slots are compared after non-null regardless of direction.
        names.Should().Equal("Carol", "Alice", "Bob");
    }

    [Fact]
    public void Order_by_entity_id_sorts_ascending()
    {
        VertexId first, second;
        using (var tx = _db.BeginWriteTransaction())
        {
            first  = AddPerson(tx, "Alice");
            second = AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var ids = g.Vertices().HasLabel("Person").Order().Id().ToList();
        ids.Should().Equal(first.Value, second.Value);
    }

    [Fact]
    public void GroupCount_counts_by_string_property()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice", city: "Tokyo");
            AddPerson(tx, "Bob",   city: "Tokyo");
            AddPerson(tx, "Carol", city: "Osaka");
            AddPerson(tx, "Dan"); // no city
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var counts = g.Vertices().HasLabel("Person").GroupCount("city");
        counts["Tokyo"].Should().Be(2);
        counts["Osaka"].Should().Be(1);
        counts.Should().NotContainKey("");
        counts.Sum(kv => kv.Value).Should().Be(3);
    }

    [Fact]
    public void Fold_returns_the_same_list_as_ToList()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            AddPerson(tx, "Alice");
            AddPerson(tx, "Bob");
            tx.Commit();
        }
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var asList = g.Vertices().HasLabel("Person").Values("name").ToList();
        var asFold = g.Vertices().HasLabel("Person").Values("name").Fold();
        asFold.Should().Equal(asList);
    }
}
