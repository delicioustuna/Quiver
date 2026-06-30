using FluentAssertions;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>
/// Match DSL パターンコンパイルテスト。GraphPattern 構築、Where 述語蓄積、
/// Return 射影の正しさを検証する。
/// </summary>
public sealed class MatchPatternTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public MatchPatternTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_match_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        tx.SetProperty(alice, "Name", PropertyValue.FromString("Alice"));
        tx.SetProperty(alice, "Age", PropertyValue.FromInt32(30));

        var bob = tx.CreateNode("Person");
        tx.SetProperty(bob, "Name", PropertyValue.FromString("Bob"));
        tx.SetProperty(bob, "Age", PropertyValue.FromInt32(25));

        var carol = tx.CreateNode("Person");
        tx.SetProperty(carol, "Name", PropertyValue.FromString("Carol"));
        tx.SetProperty(carol, "Age", PropertyValue.FromInt32(35));

        tx.CreateRelationship(alice, bob, "KNOWS");
        tx.CreateRelationship(bob, carol, "KNOWS");
        tx.CreateRelationship(alice, carol, "LIKES");

        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── GraphPattern 構築 ────────────────────────────────────────────

    [Fact]
    public void Node_creates_pattern_with_variable()
    {
        var n = GraphPattern.Node("n", "Person");
        n.Variable.Should().Be("n");
        n.Label.Should().Be("Person");
    }

    [Fact]
    public void Node_without_label()
    {
        var n = GraphPattern.Node("x");
        n.Variable.Should().Be("x");
        n.Label.Should().BeNull();
    }

    [Fact]
    public void Out_creates_directed_pattern()
    {
        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);
        pattern.Should().NotBeNull();
    }

    [Fact]
    public void In_creates_reverse_pattern()
    {
        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.In("KNOWS", b);
        pattern.Should().NotBeNull();
    }

    // ── Match query 実行 ─────────────────────────────────────────────

    [Fact]
    public void Match_returns_matching_pairs()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var results = g.Match(pattern)
            .Return(ctx => (
                SourceName: ctx["a"].Get<string>("Name"),
                TargetName: ctx["b"].Get<string>("Name")))
            .ToList();

        results.Should().HaveCount(2);
        results.Select(r => r.SourceName).Should().Contain("Alice");
        results.Select(r => r.TargetName).Should().Contain("Bob");
    }

    [Fact]
    public void Match_with_Where_filters_results()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var results = g.Match(pattern)
            .Where("a", "Name", P.Eq("Alice"))
            .Return(ctx => ctx["b"].Get<string>("Name"))
            .ToList();

        results.Should().ContainSingle().Which.Should().Be("Bob");
    }

    [Fact]
    public void Match_Count_returns_match_count()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var count = g.Match(pattern).Count();
        count.Should().Be(2); // Alice->Bob, Bob->Carol
    }

    [Fact]
    public void Match_Return_First_returns_first_match()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var first = g.Match(pattern)
            .Return(ctx => ctx["a"].Get<string>("Name"))
            .First();

        first.Should().NotBeNull();
    }

    [Fact]
    public void Match_Return_First_returns_null_on_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("GHOST", b);

        var first = g.Match(pattern)
            .Return(ctx => ctx["a"].Get<string>("Name"))
            .First();

        first.Should().BeNull();
    }

    [Fact]
    public void Match_Return_AsEnumerable_streams_results()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var enumerable = g.Match(pattern)
            .Return(ctx => ctx["a"].Get<string>("Name"))
            .AsEnumerable();

        enumerable.Count().Should().Be(2);
    }

    [Fact]
    public void Match_In_direction_matches_reverse()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = b.In("KNOWS", a);

        var results = g.Match(pattern)
            .Where("a", "Name", P.Eq("Alice"))
            .Return(ctx => ctx["b"].Get<string>("Name"))
            .ToList();

        results.Should().ContainSingle().Which.Should().Be("Bob");
    }

    [Fact]
    public void Match_with_Where_range_predicate()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var results = g.Match(pattern)
            .Where("b", "Age", P.Gt(30L))
            .Return(ctx => ctx["b"].Get<string>("Name"))
            .ToList();

        results.Should().ContainSingle().Which.Should().Be("Carol");
    }

    [Fact]
    public void Match_different_edge_type()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("LIKES", b);

        var results = g.Match(pattern)
            .Return(ctx => (
                Source: ctx["a"].Get<string>("Name"),
                Target: ctx["b"].Get<string>("Name")))
            .ToList();

        results.Should().ContainSingle();
        results[0].Source.Should().Be("Alice");
        results[0].Target.Should().Be("Carol");
    }

    // ── Match cursor ─────────────────────────────────────────────────

    [Fact]
    public void Match_AsCursor_iterates_results()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);

        using var cursor = g.Match(pattern)
            .Return(ctx => ctx["a"].Get<string>("Name"))
            .AsCursor();

        var names = new List<string>();
        while (cursor.MoveNext())
            names.Add(cursor.Current);

        names.Should().HaveCount(2);
    }

    // ── MatchContext Load<T> ─────────────────────────────────────────

    [Fact]
    public void MatchContext_Load_restores_typed_entity()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var a = GraphPattern.Node("a", "Person");
        var b = GraphPattern.Node("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var results = g.Match(pattern)
            .Where("a", "Name", P.Eq("Alice"))
            .Return(ctx => ctx.Load<PersonNode>("b"))
            .ToList();

        results.Should().ContainSingle().Which.Name.Should().Be("Bob");
    }

    // ── MatchContextRow accessor 型 ──────────────────────────────────

    [Fact]
    public void MatchContextRow_Get_long_returns_correct_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var n = GraphPattern.Node("n", "Person");
        var pattern = n.Out("KNOWS", GraphPattern.Node("x"));

        var ages = g.Match(pattern)
            .Where("n", "Name", P.Eq("Alice"))
            .Return(ctx => ctx["n"].Get<long>("Age"))
            .ToList();

        ages.Should().ContainSingle().Which.Should().Be(30L);
    }

    // ── 最小 IGraphNode 型 ───────────────────────────────────────────

    private sealed class PersonNode : IGraphNode<PersonNode>
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }

        public static string GraphLabel => "Person";

        public static NodeId Insert(IGraphTransaction tx, PersonNode entity)
        {
            var id = tx.CreateNode(GraphLabel);
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Age", PropertyValue.FromInt32(entity.Age));
            return id;
        }

        public static NodeId InsertIndexed(IGraphTransaction tx, PersonNode entity) => Insert(tx, entity);

        public static PersonNode Load(IGraphTransaction tx, NodeId id)
            => new()
            {
                Name = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Name").Utf8StringValue),
                Age = tx.GetProperty(id, "Age").Int32Value,
            };

        public static void Update(IGraphTransaction tx, NodeId id, PersonNode entity)
        {
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Age", PropertyValue.FromInt32(entity.Age));
        }

        public static void Delete(IGraphTransaction tx, NodeId id) => tx.DeleteNode(id);
    }
}
