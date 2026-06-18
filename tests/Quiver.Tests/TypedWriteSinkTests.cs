using System.Text;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 型安全集合 write シンク <c>AddEdge</c> / <c>MergeEdge</c> の振る舞い検証。
/// 直積本数・プロパティ書き込み・MergeEdge 冪等性 (ON CREATE SET)・端点型制約の正常系・
/// 同一ラベル (Person→Person) で materialize-first が無限増殖しないこと・read-your-writes 整合をカバーする。
/// </summary>
public sealed class TypedWriteSinkTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public TypedWriteSinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_ws3_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── テスト用の手書き型付きフィクスチャ (Node/Rel) ──────────────────────────

    private sealed class PersonN : IGraphNode<PersonN>
    {
        public string Name { get; set; } = "";

        public static string GraphLabel => "Person";
        public static NodeId Insert(IGraphTransaction tx, PersonN e)
        {
            var id = tx.CreateNode("Person");
            tx.SetProperty(id, "Name", PropertyValue.FromString(e.Name));
            return id;
        }
        public static NodeId InsertIndexed(IGraphTransaction tx, PersonN e) => Insert(tx, e);
        public static PersonN Load(IGraphTransaction tx, NodeId id)
            => new() { Name = Encoding.UTF8.GetString(tx.GetProperty(id, "Name").Utf8StringValue) };
        public static void Update(IGraphTransaction tx, NodeId id, PersonN e)
            => tx.SetProperty(id, "Name", PropertyValue.FromString(e.Name));
        public static void Delete(IGraphTransaction tx, NodeId id) => tx.DeleteNode(id);
    }

    private sealed class ToolN : IGraphNode<ToolN>
    {
        public string Name { get; set; } = "";

        public static string GraphLabel => "Tool";
        public static NodeId Insert(IGraphTransaction tx, ToolN e)
        {
            var id = tx.CreateNode("Tool");
            tx.SetProperty(id, "Name", PropertyValue.FromString(e.Name));
            return id;
        }
        public static NodeId InsertIndexed(IGraphTransaction tx, ToolN e) => Insert(tx, e);
        public static ToolN Load(IGraphTransaction tx, NodeId id)
            => new() { Name = Encoding.UTF8.GetString(tx.GetProperty(id, "Name").Utf8StringValue) };
        public static void Update(IGraphTransaction tx, NodeId id, ToolN e)
            => tx.SetProperty(id, "Name", PropertyValue.FromString(e.Name));
        public static void Delete(IGraphTransaction tx, NodeId id) => tx.DeleteNode(id);
    }

    /// <summary>Person → Tool の型付き辺 (プロパティ Note 付き)。</summary>
    private sealed class UseRel : IGraphRelationship<UseRel, PersonN, ToolN>
    {
        public string Note { get; set; } = "";

        public static string GraphType => "USE";
        public static RelationshipId Insert(IGraphTransaction tx, NodeId from, NodeId to, UseRel e)
        {
            var id = tx.CreateRelationship(from, to, "USE");
            tx.SetProperty(id, "Note", PropertyValue.FromString(e.Note));
            return id;
        }
        public static UseRel Load(IGraphTransaction tx, RelationshipId id)
            => new() { Note = Encoding.UTF8.GetString(tx.GetProperty(id, "Note").Utf8StringValue) };
        public static void Update(IGraphTransaction tx, RelationshipId id, UseRel e)
            => tx.SetProperty(id, "Note", PropertyValue.FromString(e.Note));
        public static void Delete(IGraphTransaction tx, RelationshipId id) => tx.DeleteRelationship(id);
    }

    /// <summary>Person → Person の同一ラベル辺 (Halloween 退行テスト用)。</summary>
    private sealed class FriendRel : IGraphRelationship<FriendRel, PersonN, PersonN>
    {
        public static string GraphType => "FRIEND";
        public static RelationshipId Insert(IGraphTransaction tx, NodeId from, NodeId to, FriendRel e)
            => tx.CreateRelationship(from, to, "FRIEND");
        public static FriendRel Load(IGraphTransaction tx, RelationshipId id) => new();
        public static void Update(IGraphTransaction tx, RelationshipId id, FriendRel e) { }
        public static void Delete(IGraphTransaction tx, RelationshipId id) => tx.DeleteRelationship(id);
    }

    // ── AddEdge: 直積本数 ───────────────────────────────────────────────────────

    [Fact]
    public void AddEdge_creates_full_cartesian_product()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        for (int i = 0; i < 3; i++) PersonN.Insert(tx, new PersonN { Name = "P" + i });
        for (int j = 0; j < 4; j++) ToolN.Insert(tx, new ToolN { Name = "T" + j });

        long created = g.Nodes<PersonN>()
            .AddEdge(g.Nodes<ToolN>(), (p, t) => new UseRel { Note = $"{p.Name}->{t.Name}" });

        created.Should().Be(12);   // 3 × 4
        g.Nodes<PersonN>().OutRelationships<UseRel>().Count().Should().Be(12);
        tx.Commit();
    }

    [Fact]
    public void AddEdge_respects_Where_on_both_sides()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        foreach (var n in new[] { "Bob", "Bill", "Alice" }) PersonN.Insert(tx, new PersonN { Name = n });
        foreach (var n in new[] { "Cutter", "Compiler", "Drill" }) ToolN.Insert(tx, new ToolN { Name = n });

        // B で始まる Person (2) × C で始まる Tool (2) = 4。
        long created = g.Nodes<PersonN>().Where(p => p.Name.StartsWith("B"))
            .AddEdge(g.Nodes<ToolN>().Where(t => t.Name.StartsWith("C")),
                     (p, t) => new UseRel { Note = "auto" });

        created.Should().Be(4);
        g.Nodes<PersonN>().OutRelationships<UseRel>().Count().Should().Be(4);
        tx.Commit();
    }

    [Fact]
    public void AddEdge_writes_edge_properties()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        var alice = PersonN.Insert(tx, new PersonN { Name = "Alice" });
        ToolN.Insert(tx, new ToolN { Name = "Hammer" });

        g.Nodes<PersonN>().AddEdge(g.Nodes<ToolN>(), (p, t) => new UseRel { Note = $"{p.Name}:{t.Name}" });

        var relId = g.Node(alice).OutRelationships<UseRel>().ToList().Single();
        UseRel.Load(tx, relId).Note.Should().Be("Alice:Hammer");
        tx.Commit();
    }

    [Fact]
    public void AddEdge_propless_creates_edges_without_properties()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        for (int i = 0; i < 2; i++) PersonN.Insert(tx, new PersonN { Name = "P" + i });
        for (int j = 0; j < 3; j++) ToolN.Insert(tx, new ToolN { Name = "T" + j });

        long created = g.Nodes<PersonN>().AddEdge<PersonN, UseRel, ToolN>(g.Nodes<ToolN>());

        created.Should().Be(6);
        g.Nodes<PersonN>().OutRelationships<UseRel>().Count().Should().Be(6);
        tx.Commit();
    }

    [Fact]
    public void AddEdge_correlated_evaluates_targets_per_source()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        PersonN.Insert(tx, new PersonN { Name = "Bob" });
        PersonN.Insert(tx, new PersonN { Name = "Carol" });
        foreach (var n in new[] { "Cutter", "Compiler", "Drill" }) ToolN.Insert(tx, new ToolN { Name = n });

        // 各 Person について、名前の頭文字で始まる Tool だけに辺を張る相関版。
        long created = g.Nodes<PersonN>()
            .AddEdge(p => g.Nodes<ToolN>().Where(t => t.Name.StartsWith(p.Name.Substring(0, 1))),
                     (p, t) => new UseRel { Note = p.Name });

        // Bob → (B で始まる Tool は無し = 0) / Carol → (Cutter, Compiler = 2)
        created.Should().Be(2);
        g.Nodes<PersonN>().OutRelationships<UseRel>().Count().Should().Be(2);
        tx.Commit();
    }

    // ── MergeEdge: 冪等性 / ON CREATE SET ──────────────────────────────────────

    [Fact]
    public void MergeEdge_is_idempotent()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        for (int i = 0; i < 2; i++) PersonN.Insert(tx, new PersonN { Name = "P" + i });
        for (int j = 0; j < 3; j++) ToolN.Insert(tx, new ToolN { Name = "T" + j });

        var first  = g.Nodes<PersonN>().MergeEdge(g.Nodes<ToolN>(), (p, t) => new UseRel { Note = "x" });
        var second = g.Nodes<PersonN>().MergeEdge(g.Nodes<ToolN>(), (p, t) => new UseRel { Note = "x" });

        first.Should().Be((6L, 0L));    // 全て新規
        second.Should().Be((0L, 6L));   // 全て既存ヒット
        g.Nodes<PersonN>().OutRelationships<UseRel>().Count().Should().Be(6);
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_sets_properties_on_create_only()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        var alice = PersonN.Insert(tx, new PersonN { Name = "Alice" });
        ToolN.Insert(tx, new ToolN { Name = "Hammer" });

        g.Nodes<PersonN>().MergeEdge(g.Nodes<ToolN>(), (p, t) => new UseRel { Note = "first" });
        var second = g.Nodes<PersonN>().MergeEdge(g.Nodes<ToolN>(), (p, t) => new UseRel { Note = "second" });

        second.Should().Be((0L, 1L));
        var relId = g.Node(alice).OutRelationships<UseRel>().ToList().Single();
        UseRel.Load(tx, relId).Note.Should().Be("first");   // 既存辺のプロパティは保持される
        tx.Commit();
    }

    [Fact]
    public void MergeEdge_propless_is_idempotent()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        for (int i = 0; i < 2; i++) PersonN.Insert(tx, new PersonN { Name = "P" + i });
        for (int j = 0; j < 2; j++) ToolN.Insert(tx, new ToolN { Name = "T" + j });

        var first  = g.Nodes<PersonN>().MergeEdge<PersonN, UseRel, ToolN>(g.Nodes<ToolN>());
        var second = g.Nodes<PersonN>().MergeEdge<PersonN, UseRel, ToolN>(g.Nodes<ToolN>());

        first.Should().Be((4L, 0L));
        second.Should().Be((0L, 4L));
        tx.Commit();
    }

    // ── Halloween 退行: source == target ラベル ────────────────────────────────

    [Fact]
    public void SameLabel_AddEdge_is_bounded_and_terminates()
    {
        // materialize-first: 両端を先に確定するため、辺を書いても始点/終点集合は増えない。
        // naive な遅延実装なら read-your-writes で増殖し得る形を N×N に固定して退行を防ぐ。
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        const int n = 4;
        for (int i = 0; i < n; i++) PersonN.Insert(tx, new PersonN { Name = "P" + i });

        long created = g.Nodes<PersonN>()
            .AddEdge(g.Nodes<PersonN>(), (a, b) => new FriendRel());

        created.Should().Be(n * n);   // 自己ペアを含む完全直積。無限増殖しない。
        g.Nodes<PersonN>().OutRelationships<FriendRel>().Count().Should().Be(n * n);
        tx.Commit();
    }

    [Fact]
    public void SameLabel_correlated_AddEdge_does_not_feed_back()
    {
        // 相関版で終点が「既存 FRIEND 辺の先」を辿る形。始点リストを先に確定するため、
        // ループ中に書いた辺が始点集合を増やすことはなく有限で停止する。
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        const int n = 3;
        for (int i = 0; i < n; i++) PersonN.Insert(tx, new PersonN { Name = "P" + i });

        long created = g.Nodes<PersonN>()
            .AddEdge<PersonN, FriendRel, PersonN>(p => g.Nodes<PersonN>());

        created.Should().Be(n * n);
        tx.Commit();
    }

    // ── read-your-writes 整合 ──────────────────────────────────────────────────

    [Fact]
    public void AddEdge_results_visible_within_same_tx()
    {
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        var alice = PersonN.Insert(tx, new PersonN { Name = "Alice" });
        for (int j = 0; j < 3; j++) ToolN.Insert(tx, new ToolN { Name = "T" + j });

        g.Nodes<PersonN>().AddEdge(g.Nodes<ToolN>(), (p, t) => new UseRel { Note = "x" });

        // 同一 tx 内で直後に辿って 3 件見える (read-your-writes)。
        g.Node(alice).Out<UseRel>().ToList().Should().HaveCount(3);
        tx.Commit();
    }

    // ── 端点型制約: 正常系がコンパイルできること ───────────────────────────────

    [Fact]
    public void EndpointTypeConstraint_valid_direction_compiles()
    {
        // Person → USE → Tool は IGraphRelationship<UseRel, PersonN, ToolN> 制約を満たすので通る。
        // 誤った向き (例 g.Nodes<ToolN>().AddEdge(g.Nodes<PersonN>(), (t,p) => new UseRel{...})) は
        // コンパイルエラーになる (制約 TSource=ToolN が IGraphRelationship<UseRel,ToolN,...> を満たさない)。
        using var tx = _db.BeginTransaction();
        var g = tx.G(_db.Schema);
        PersonN.Insert(tx, new PersonN { Name = "Alice" });
        ToolN.Insert(tx, new ToolN { Name = "Hammer" });

        long created = g.Nodes<PersonN>().AddEdge(g.Nodes<ToolN>(), (p, t) => new UseRel { Note = "ok" });

        created.Should().Be(1);
        tx.Commit();
    }
}
