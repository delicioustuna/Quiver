using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>
/// TypedGraphTraversal の型付きフィルタ・ホップ・終端テスト。
/// 式ツリーベースの Has / Where、型保存ホップ、ToList / First / Count を検証する。
/// </summary>
public sealed class TypedTraversalTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public TypedTraversalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_typed_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);

        var alice = g.Insert(new PersonModel { Name = "Alice", Age = 30 });
        var bob = g.Insert(new PersonModel { Name = "Bob", Age = 25 });
        var carol = g.Insert(new PersonModel { Name = "Carol", Age = 35 });

        tx.CreateEdge(alice, bob, "KNOWS");
        tx.CreateEdge(bob, carol, "KNOWS");

        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── Vertices<T>() 型付き scan ──────────────────────────────────────

    [Fact]
    public void Vertices_typed_returns_all_of_type()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Vertices<PersonModel>().ToList();
        all.Should().HaveCount(3);
        all.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Name));
    }

    // ── Has<TProp> 式ベースの filter ────────────────────────────────

    [Fact]
    public void Has_expression_string_filters()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Has(p => p.Name, "Alice")
            .ToList();
        result.Should().ContainSingle().Which.Name.Should().Be("Alice");
    }

    [Fact]
    public void Has_expression_int_filters()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Has(p => p.Age, 25)
            .ToList();
        result.Should().ContainSingle().Which.Name.Should().Be("Bob");
    }

    [Fact]
    public void Has_expression_predicate_filters()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Has(p => p.Age, P.Gt(28L))
            .ToList();
        result.Should().HaveCount(2);
        result.Select(p => p.Name).Should().BeEquivalentTo("Alice", "Carol");
    }

    // ── Where 式ベースの filter ─────────────────────────────────────

    [Fact]
    public void Where_expression_filters()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Where(p => p.Age > 28 && p.Age < 34)
            .ToList();
        result.Should().ContainSingle().Which.Name.Should().Be("Alice");
    }

    // ── Values (式 selector) ─────────────────────────────────────────

    [Fact]
    public void Values_expression_extracts_property()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var names = g.Vertices<PersonModel>()
            .Values(p => p.Name)
            .ToList();
        names.Should().BeEquivalentTo("Alice", "Bob", "Carol");
    }

    // ── Out / In / Both (型なしへの downgrade) ──────────────────────

    [Fact]
    public void Out_untyped_from_typed_traversal()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Has(p => p.Name, "Alice")
            .Out("KNOWS")
            .ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void In_untyped_from_typed_traversal()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Has(p => p.Name, "Bob")
            .In("KNOWS")
            .ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Both_untyped_from_typed_traversal()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Has(p => p.Name, "Bob")
            .Both("KNOWS")
            .ToList();
        result.Should().HaveCount(2);
    }

    // ── OutEdges / InEdges ────────────────────────────

    [Fact]
    public void OutEdges_from_typed_traversal()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var edges = g.Vertices<PersonModel>()
            .Has(p => p.Name, "Alice")
            .OutEdges("KNOWS")
            .ToList();
        edges.Should().ContainSingle();
    }

    [Fact]
    public void InEdges_from_typed_traversal()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var edges = g.Vertices<PersonModel>()
            .Has(p => p.Name, "Bob")
            .InEdges("KNOWS")
            .ToList();
        edges.Should().ContainSingle();
    }

    // ── 終端操作 ─────────────────────────────────────────────────────

    [Fact]
    public void First_returns_first_entity()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var first = g.Vertices<PersonModel>().Has(p => p.Name, "Alice").First();
        first.Should().NotBeNull();
        first!.Name.Should().Be("Alice");
    }

    [Fact]
    public void First_returns_null_on_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Vertices<GhostModel>().Count().Should().Be(0);
        g.Vertices<GhostModel>().ToList().Should().BeEmpty();
    }

    [Fact]
    public void Count_returns_correct_count()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Vertices<PersonModel>().Count().Should().Be(3);
    }

    [Fact]
    public void ToListWithIds_returns_id_entity_pairs()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var pairs = g.Vertices<PersonModel>().ToListWithIds();
        pairs.Should().HaveCount(3);
        pairs.Should().OnlyContain(p => p.Id.IsValid && p.Entity.Name != null);
    }

    // ── 型付き filter の連結 ─────────────────────────────────────────

    [Fact]
    public void Multiple_Has_chains_as_implicit_AND()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Has(p => p.Name, "Alice")
            .Has(p => p.Age, 30)
            .ToList();
        result.Should().ContainSingle().Which.Name.Should().Be("Alice");
    }

    [Fact]
    public void Has_then_Where_chains_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices<PersonModel>()
            .Has(p => p.Age, P.Gte(25L))
            .Where(p => p.Name.StartsWith("A"))
            .ToList();
        result.Should().ContainSingle().Which.Name.Should().Be("Alice");
    }

    // ── 最小 IGraphVertex 型 ───────────────────────────────────────────

    private sealed class PersonModel : IGraphVertex<PersonModel>
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }

        public static string GraphLabel => "Person";

        public static VertexId Insert(IGraphTransaction tx, PersonModel entity)
        {
            var id = tx.CreateVertex(GraphLabel);
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Age", PropertyValue.FromInt32(entity.Age));
            return id;
        }

        public static VertexId InsertIndexed(IGraphTransaction tx, PersonModel entity) => Insert(tx, entity);

        public static PersonModel Load(IGraphTransaction tx, VertexId id)
            => new()
            {
                Name = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Name").Utf8StringValue),
                Age = tx.GetProperty(id, "Age").Int32Value,
            };

        public static void Update(IGraphTransaction tx, VertexId id, PersonModel entity)
        {
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Age", PropertyValue.FromInt32(entity.Age));
        }

        public static void Delete(IGraphTransaction tx, VertexId id) => tx.DeleteVertex(id);
    }

    private sealed class GhostModel : IGraphVertex<GhostModel>
    {
        public static string GraphLabel => "Ghost";
        public static VertexId Insert(IGraphTransaction tx, GhostModel entity) => tx.CreateVertex(GraphLabel);
        public static VertexId InsertIndexed(IGraphTransaction tx, GhostModel entity) => Insert(tx, entity);
        public static GhostModel Load(IGraphTransaction tx, VertexId id) => new();
        public static void Update(IGraphTransaction tx, VertexId id, GhostModel entity) { }
        public static void Delete(IGraphTransaction tx, VertexId id) => tx.DeleteVertex(id);
    }
}
