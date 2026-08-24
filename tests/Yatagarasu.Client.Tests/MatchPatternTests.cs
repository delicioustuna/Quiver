using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Api.Match;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Api.Tests;

/// <summary>
/// Match DSL パターンコンパイルテスト。GraphPattern 構築、Where 述語蓄積、
/// Return 射影の正しさを検証する。
/// </summary>
public sealed class MatchPatternTests : IDisposable
{
    private readonly string _dir;
    private readonly YatagarasuDatabase _db;

    public MatchPatternTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_match_" + Guid.NewGuid().ToString("N"));
        _db = YatagarasuDatabase.Open(Path.Combine(_dir, "graph.yata"));

        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        tx.SetProperty(alice, "Name", PropertyValue.FromString("Alice"));
        tx.SetProperty(alice, "Age", PropertyValue.FromInt32(30));

        var bob = tx.CreateVertex("Person");
        tx.SetProperty(bob, "Name", PropertyValue.FromString("Bob"));
        tx.SetProperty(bob, "Age", PropertyValue.FromInt32(25));

        var carol = tx.CreateVertex("Person");
        tx.SetProperty(carol, "Name", PropertyValue.FromString("Carol"));
        tx.SetProperty(carol, "Age", PropertyValue.FromInt32(35));

        tx.CreateEdge(alice, bob, "KNOWS");
        tx.CreateEdge(bob, carol, "KNOWS");
        tx.CreateEdge(alice, carol, "LIKES");

        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── GraphPattern 構築 ────────────────────────────────────────────

    [Fact]
    public void Vertex_creates_pattern_with_variable()
    {
        var n = GraphPattern.Vertex("n", "Person");
        n.Variable.Should().Be("n");
        n.Label.Should().Be("Person");
    }

    [Fact]
    public void Vertex_without_label()
    {
        var n = GraphPattern.Vertex("x");
        n.Variable.Should().Be("x");
        n.Label.Should().BeNull();
    }

    [Fact]
    public void Out_creates_directed_pattern()
    {
        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
        var pattern = a.Out("KNOWS", b);
        pattern.Should().NotBeNull();
    }

    [Fact]
    public void In_creates_reverse_pattern()
    {
        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
        var pattern = a.In("KNOWS", b);
        pattern.Should().NotBeNull();
    }

    // ── Match query 実行 ─────────────────────────────────────────────

    [Fact]
    public void Match_returns_matching_pairs()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
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
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
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
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var count = g.Match(pattern).Count();
        count.Should().Be(2); // Alice->Bob, Bob->Carol
    }

    [Fact]
    public void Match_Return_First_returns_first_match()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var first = g.Match(pattern)
            .Return(ctx => ctx["a"].Get<string>("Name"))
            .First();

        first.Should().NotBeNull();
    }

    [Fact]
    public void Match_Return_First_returns_null_on_empty()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
        var pattern = a.Out("GHOST", b);

        var first = g.Match(pattern)
            .Return(ctx => ctx["a"].Get<string>("Name"))
            .First();

        first.Should().BeNull();
    }

    [Fact]
    public void Match_Return_AsEnumerable_streams_results()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var enumerable = g.Match(pattern)
            .Return(ctx => ctx["a"].Get<string>("Name"))
            .AsEnumerable();

        enumerable.Count().Should().Be(2);
    }

    [Fact]
    public void Match_In_direction_matches_reverse()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
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
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
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
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
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
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
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
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var a = GraphPattern.Vertex("a", "Person");
        var b = GraphPattern.Vertex("b", "Person");
        var pattern = a.Out("KNOWS", b);

        var results = g.Match(pattern)
            .Where("a", "Name", P.Eq("Alice"))
            .Return(ctx => ctx.Load<PersonVertex>("b"))
            .ToList();

        results.Should().ContainSingle().Which.Name.Should().Be("Bob");
    }

    // ── MatchContextRow accessor 型 ──────────────────────────────────

    [Fact]
    public void MatchContextRow_Get_long_returns_correct_value()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var n = GraphPattern.Vertex("n", "Person");
        var pattern = n.Out("KNOWS", GraphPattern.Vertex("x"));

        var ages = g.Match(pattern)
            .Where("n", "Name", P.Eq("Alice"))
            .Return(ctx => ctx["n"].Get<long>("Age"))
            .ToList();

        ages.Should().ContainSingle().Which.Should().Be(30L);
    }

    // ── 星型Nexusパターン ───────────────────────────────────

    // subject/object/source/asOf の 4 role を持つ Fact Nexus 2 件を用意する。
    // 2 件目は object role に 2 メンバー (同一 role 複数メンバー) を持たせる。
    private (NexusId Verified, NexusId Draft, VertexId Alice, VertexId Bob,
             VertexId Yatagarasu, VertexId GraphDb, VertexId Chunk, VertexId AsOf) SeedFacts()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Entity");
        tx.SetProperty(alice, "Name", PropertyValue.FromString("Alice"));
        var bob = tx.CreateVertex("Entity");
        tx.SetProperty(bob, "Name", PropertyValue.FromString("Bob"));
        var yatagarasu = tx.CreateVertex("Entity");
        tx.SetProperty(yatagarasu, "Name", PropertyValue.FromString("Yatagarasu"));
        var graphDb = tx.CreateVertex("Entity");
        tx.SetProperty(graphDb, "Name", PropertyValue.FromString("YatagarasuDatabase"));
        var chunk = tx.CreateVertex("Chunk");
        var asOf = tx.CreateVertex("TimePoint");

        var verified = tx.CreateNexus("Fact",
        [
            new NexusMember("subject", alice),
            new NexusMember("object", yatagarasu),
            new NexusMember("source", chunk),
            new NexusMember("asOf", asOf),
        ]);
        tx.SetProperty(verified, "status", PropertyValue.FromString("verified"));

        var draft = tx.CreateNexus("Fact",
        [
            new NexusMember("subject", bob),
            new NexusMember("object", yatagarasu),
            new NexusMember("object", graphDb),
            new NexusMember("source", chunk),
            new NexusMember("asOf", asOf),
        ]);
        tx.SetProperty(draft, "status", PropertyValue.FromString("draft"));
        tx.Commit();
        return (verified, draft, alice, bob, yatagarasu, graphDb, chunk, asOf);
    }

    [Fact]
    public void Nexus_pattern_binds_two_members_to_same_row()
    {
        var seed = SeedFacts();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("s"))
            .Member("object", GraphPattern.Vertex("o"));

        var rows = g.Match(pattern)
            .Return(ctx => (Subject: ctx.Vertex("s"), Object: ctx.Vertex("o")))
            .ToList();

        // verified: (Alice, Yatagarasu)。draft: (Bob, Yatagarasu), (Bob, GraphDb)。
        rows.Should().BeEquivalentTo(new[]
        {
            (seed.Alice, seed.Yatagarasu),
            (seed.Bob, seed.Yatagarasu),
            (seed.Bob, seed.GraphDb),
        });
    }

    [Fact]
    public void Nexus_pattern_binds_four_members_and_nexus()
    {
        var seed = SeedFacts();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("s"))
            .Member("object", GraphPattern.Vertex("o"))
            .Member("source", GraphPattern.Vertex("src"))
            .Member("asOf", GraphPattern.Vertex("t"));

        var rows = g.Match(pattern)
            .Where("f", "status", P.Eq("verified"))
            .Return(ctx => (
                Fact: ctx.Nexus("f"),
                Subject: ctx.Vertex("s"),
                Object: ctx.Vertex("o"),
                Source: ctx.Vertex("src"),
                AsOf: ctx.Vertex("t")))
            .ToList();

        rows.Should().ContainSingle().Which.Should().Be(
            (seed.Verified, seed.Alice, seed.Yatagarasu, seed.Chunk, seed.AsOf));
    }

    [Fact]
    public void Nexus_pattern_supports_variable_member_count()
    {
        var seed = SeedFacts();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        // 3 メンバー (subject/object/source) の可変個ケース。
        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("s"))
            .Member("object", GraphPattern.Vertex("o"))
            .Member("source", GraphPattern.Vertex("src"));

        var count = g.Match(pattern).Count();

        // verified: 1 (object=Yatagarasu)。draft: 2 (object=Yatagarasu, GraphDb)。source は各 1。
        count.Should().Be(3);
    }

    [Fact]
    public void Nexus_pattern_applies_member_label_filter()
    {
        var seed = SeedFacts();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        // anchor に Chunk ラベルを課すと Fact は source メンバーが Chunk のものだけ残る。
        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("source", GraphPattern.Vertex("src", "Chunk"))
            .Member("subject", GraphPattern.Vertex("s", "Entity"));

        var subjects = g.Match(pattern)
            .Return(ctx => ctx.Vertex("s"))
            .ToList();

        subjects.Should().BeEquivalentTo(new[] { seed.Alice, seed.Bob });
    }

    [Fact]
    public void Nexus_pattern_filters_by_vertex_and_nexus_property()
    {
        var seed = SeedFacts();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("s"))
            .Member("object", GraphPattern.Vertex("o"));

        var rows = g.Match(pattern)
            .Where("f", "status", P.Eq("draft"))       // nexus property
            .Where("s", "Name", P.Eq("Bob"))            // vertex property
            .Return(ctx => ctx.Vertex("o"))
            .ToList();

        // draft の subject=Bob。object は Yatagarasu と GraphDb の 2 件。
        rows.Should().BeEquivalentTo(new[] { seed.Yatagarasu, seed.GraphDb });
    }

    [Fact]
    public void Nexus_pattern_same_role_multiple_candidates_emits_each()
    {
        var seed = SeedFacts();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        // draft は object role に 2 メンバー。同じ nexus 内で組み合わせを放出する。
        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("s", "Entity"))
            .Member("object", GraphPattern.Vertex("o"));

        var objects = g.Match(pattern)
            .Where("s", "Name", P.Eq("Bob"))
            .Return(ctx => ctx.Vertex("o"))
            .ToList();

        objects.Should().BeEquivalentTo(new[] { seed.Yatagarasu, seed.GraphDb });
    }

    [Fact]
    public void Nexus_pattern_rejects_duplicate_variable()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("x"))
            .Member("object", GraphPattern.Vertex("x"));

        Action act = () => g.Match(pattern).Count();
        act.Should().Throw<InvalidOperationException>().WithMessage("*x*");
    }

    [Fact]
    public void Nexus_pattern_rejects_variable_colliding_with_nexus()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("f"));

        Action act = () => g.Match(pattern).Count();
        act.Should().Throw<InvalidOperationException>().WithMessage("*f*");
    }

    [Fact]
    public void Nexus_pattern_rejects_empty_role()
    {
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("", GraphPattern.Vertex("s"));

        Action act = () => g.Match(pattern).Count();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Nexus_pattern_rejects_unknown_where_variable()
    {
        var seed = SeedFacts();
        using var tx = _db.BeginReadTransaction();
        var g = tx.Query;

        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("s"));

        Action act = () => g.Match(pattern)
            .Where("ghost", "Name", P.Eq("Alice"))
            .Return(ctx => ctx.Vertex("s"))
            .ToList();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ghost*");
    }

    // ── 最小 IGraphVertex 型 ───────────────────────────────────────────

    private sealed class PersonVertex : IGraphVertex<PersonVertex>
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }

        public static string GraphLabel => "Person";

        public static VertexId Insert(IWriteTransaction tx, PersonVertex entity)
        {
            var id = tx.CreateVertex(GraphLabel);
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Age", PropertyValue.FromInt32(entity.Age));
            return id;
        }

        public static VertexId InsertIndexed(IWriteTransaction tx, PersonVertex entity) => Insert(tx, entity);

        public static PersonVertex Load(IReadTransaction tx, VertexId id)
            => new()
            {
                Name = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Name").Utf8StringValue),
                Age = tx.GetProperty(id, "Age").Int32Value,
            };

        public static void Update(IWriteTransaction tx, VertexId id, PersonVertex entity)
        {
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Age", PropertyValue.FromInt32(entity.Age));
        }

        public static void Delete(IWriteTransaction tx, VertexId id) => tx.DeleteVertex(id);
    }
}
