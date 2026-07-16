using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>
/// マルチバリュープロパティ (MV トラック) の DSL テスト。
/// AddPropertyValue / RemovePropertyValue / GetPropertyValues の API、
/// TypedGraphTraversal の List&lt;T&gt; プロパティアクセス、
/// Set cardinality 包含クエリの DSL を検証する。
/// 統合テスト (MultiValuePropertyTests) と重複しない DSL 構築の単体テストに注力する。
/// </summary>
public sealed class MultiValueDslTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public MultiValueDslTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_mv_dsl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── DSL で multi-value set を作る AddPropertyValue ──────────────

    [Fact]
    public void AddPropertyValue_string_builds_set()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("alpha"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("beta"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("gamma"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = CollectStrings(ro.GetPropertyValues(n, "tags"));
        values.Should().HaveCount(3);
        values.Should().BeEquivalentTo("alpha", "beta", "gamma");
    }

    [Fact]
    public void RemovePropertyValue_string_removes_from_set()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("alpha"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("beta"));
        tx.RemovePropertyValue(n, "tags", PropertyValue.FromString("alpha"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = CollectStrings(ro.GetPropertyValues(n, "tags"));
        values.Should().ContainSingle().Which.Should().Be("beta");
    }

    [Fact]
    public void GetPropertyValues_on_empty_set_returns_empty()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = CollectStrings(ro.GetPropertyValues(n, "tags"));
        values.Should().BeEmpty();
    }

    // ── Set cardinality の Has traversal (包含 query) ───────────────

    [Fact]
    public void Has_containment_query_finds_vertices_with_matching_value()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n1 = tx.CreateVertex("Item");
        tx.AddPropertyValue(n1, "tags", PropertyValue.FromString("red"));
        tx.AddPropertyValue(n1, "tags", PropertyValue.FromString("blue"));
        var n2 = tx.CreateVertex("Item");
        tx.AddPropertyValue(n2, "tags", PropertyValue.FromString("green"));
        tx.AddPropertyValue(n2, "tags", PropertyValue.FromString("blue"));
        var n3 = tx.CreateVertex("Item");
        tx.AddPropertyValue(n3, "tags", PropertyValue.FromString("red"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var g = ro.G(db.Schema);

        // "red" -> n1, n3
        var reds = g.Vertices().HasLabel("Item").Has("tags", "red").ToList();
        reds.Should().HaveCount(2);
        reds.Should().Contain(n1);
        reds.Should().Contain(n3);

        // "blue" -> n1, n2
        var blues = g.Vertices().HasLabel("Item").Has("tags", "blue").ToList();
        blues.Should().HaveCount(2);
        blues.Should().Contain(n1);
        blues.Should().Contain(n2);

        // "green" -> n2
        var greens = g.Vertices().HasLabel("Item").Has("tags", "green").ToList();
        greens.Should().ContainSingle().Which.Should().Be(n2);
    }

    [Fact]
    public void Has_containment_query_nonexistent_value_returns_empty()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("red"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var g = ro.G(db.Schema);
        g.Vertices().HasLabel("Item").Has("tags", "nonexistent").ToList().Should().BeEmpty();
    }

    // ── Has 包含と他の filter の chain ───────────────────────────────

    [Fact]
    public void Has_containment_chains_with_HasLabel()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n1 = tx.CreateVertex("Item");
        tx.AddPropertyValue(n1, "tags", PropertyValue.FromString("red"));
        var n2 = tx.CreateVertex("Other");
        tx.AddPropertyValue(n2, "tags", PropertyValue.FromString("red"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var g = ro.G(db.Schema);
        var result = g.Vertices().HasLabel("Item").Has("tags", "red").ToList();
        result.Should().ContainSingle().Which.Should().Be(n1);
    }

    [Fact]
    public void Has_containment_chains_with_Has_single_value()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n1 = tx.CreateVertex("Item");
        tx.SetProperty(n1, "Color", PropertyValue.FromString("bright"));
        tx.AddPropertyValue(n1, "tags", PropertyValue.FromString("red"));
        var n2 = tx.CreateVertex("Item");
        tx.SetProperty(n2, "Color", PropertyValue.FromString("dark"));
        tx.AddPropertyValue(n2, "tags", PropertyValue.FromString("red"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var g = ro.G(db.Schema);
        var result = g.Vertices().HasLabel("Item")
            .Has("tags", "red")
            .Has("Color", "bright")
            .ToList();
        result.Should().ContainSingle().Which.Should().Be(n1);
    }

    // ── List<T> 用 TypedGraphTraversal Has<TElem> ────────────────────

    [Fact]
    public void TypedTraversal_Has_List_string_containment()
    {
        using var db = QuiverDatabase.Open(_path);
        db.EnsureIndexes<TaggedItem>();

        using (var tx = db.BeginTransaction())
        {
            var g = tx.G(db.Schema);
            g.InsertIndexed(new TaggedItem { Label = "A", Tags = ["x", "y"] });
            g.InsertIndexed(new TaggedItem { Label = "B", Tags = ["y", "z"] });
            g.InsertIndexed(new TaggedItem { Label = "C", Tags = ["z"] });
            tx.Commit();
        }

        using var ro = db.BeginReadOnlyTransaction();
        var g2 = ro.G(db.Schema);

        var yItems = g2.Vertices<TaggedItem>()
            .Has(i => i.Tags, "y")
            .ToList();
        yItems.Select(i => i.Label).Should().BeEquivalentTo("A", "B");

        var zItems = g2.Vertices<TaggedItem>()
            .Has(i => i.Tags, "z")
            .ToList();
        zItems.Select(i => i.Label).Should().BeEquivalentTo("B", "C");
    }

    // ── TypedGraphTraversal Values<List<TElem>> ──────────────────────

    [Fact]
    public void TypedTraversal_Values_List_returns_per_vertex_lists()
    {
        using var db = QuiverDatabase.Open(_path);
        db.EnsureIndexes<TaggedItem>();

        using (var tx = db.BeginTransaction())
        {
            var g = tx.G(db.Schema);
            g.InsertIndexed(new TaggedItem { Label = "A", Tags = ["x", "y"] });
            g.InsertIndexed(new TaggedItem { Label = "B", Tags = ["z"] });
            tx.Commit();
        }

        using var ro = db.BeginReadOnlyTransaction();
        var allTags = ro.G(db.Schema)
            .Vertices<TaggedItem>()
            .Values(i => i.Tags)
            .ToList();

        allTags.Should().HaveCount(2);
        allTags.SelectMany(t => t).Should().BeEquivalentTo("x", "y", "z");
    }

    [Fact]
    public void TypedTraversal_Values_List_empty_tags_returns_empty_list()
    {
        using var db = QuiverDatabase.Open(_path);
        db.EnsureIndexes<TaggedItem>();

        using (var tx = db.BeginTransaction())
        {
            var g = tx.G(db.Schema);
            g.InsertIndexed(new TaggedItem { Label = "A", Tags = [] });
            tx.Commit();
        }

        using var ro = db.BeginReadOnlyTransaction();
        var tags = ro.G(db.Schema)
            .Vertices<TaggedItem>()
            .Values(i => i.Tags)
            .ToList();

        tags.Should().ContainSingle().Which.Should().BeEmpty();
    }

    // ── TypedGraphTraversal の Has 包含と Where の chain ─────────────

    [Fact]
    public void TypedTraversal_Has_List_chains_with_Where_expression()
    {
        using var db = QuiverDatabase.Open(_path);
        db.EnsureIndexes<TaggedItem>();

        using (var tx = db.BeginTransaction())
        {
            var g = tx.G(db.Schema);
            g.InsertIndexed(new TaggedItem { Label = "A", Priority = 1, Tags = ["important"] });
            g.InsertIndexed(new TaggedItem { Label = "B", Priority = 2, Tags = ["important"] });
            g.InsertIndexed(new TaggedItem { Label = "C", Priority = 3, Tags = ["minor"] });
            tx.Commit();
        }

        using var ro = db.BeginReadOnlyTransaction();
        var result = ro.G(db.Schema)
            .Vertices<TaggedItem>()
            .Has(i => i.Tags, "important")
            .Where(i => i.Priority > 1)
            .ToList();

        result.Should().ContainSingle().Which.Label.Should().Be("B");
    }

    // ── 異なる要素型を使う TypedGraphTraversal Has ──────────────────

    [Fact]
    public void TypedTraversal_Has_List_int_containment()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("scores", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Data");
        tx.SetProperty(n, "Name", PropertyValue.FromString("test"));
        tx.AddPropertyValue(n, "scores", PropertyValue.FromInt32(10));
        tx.AddPropertyValue(n, "scores", PropertyValue.FromInt32(20));
        tx.AddPropertyValue(n, "scores", PropertyValue.FromInt32(30));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var g = ro.G(db.Schema);
        var found = g.Vertices().HasLabel("Data").Has("scores", 20).ToList();
        found.Should().ContainSingle().Which.Should().Be(n);

        var notFound = g.Vertices().HasLabel("Data").Has("scores", 99).ToList();
        notFound.Should().BeEmpty();
    }

    // ── cardinality を強制する DSL layer ─────────────────────────────

    [Fact]
    public void SetProperty_on_Set_cardinality_key_throws()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        var act = () => tx.SetProperty(n, "tags", PropertyValue.FromString("x"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddPropertyValue_on_Single_cardinality_key_throws()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("name", PropertyCardinality.Single);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        var act = () => tx.AddPropertyValue(n, "name", PropertyValue.FromString("x"));
        act.Should().Throw<InvalidOperationException>();
    }

    // ── 重複 add の冪等性 (set semantics) ────────────────────────────

    [Fact]
    public void AddPropertyValue_duplicate_is_idempotent()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("x"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("x"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("y"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = CollectStrings(ro.GetPropertyValues(n, "tags"));
        values.Should().HaveCount(2);
    }

    // ── 存在しない値への RemovePropertyValue は no-op ──────────────

    [Fact]
    public void RemovePropertyValue_nonexistent_value_is_noop()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("x"));
        tx.RemovePropertyValue(n, "tags", PropertyValue.FromString("nonexistent"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = CollectStrings(ro.GetPropertyValues(n, "tags"));
        values.Should().ContainSingle().Which.Should().Be("x");
    }

    // ── 未知 key の GetPropertyValues は空を返す ────────────────────

    [Fact]
    public void GetPropertyValues_unknown_key_returns_empty()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var n = tx.CreateVertex("Item");
        var values = CollectStrings(tx.GetPropertyValues(n, "nonexistent"));
        values.Should().BeEmpty();
    }

    // ── ヘルパー ─────────────────────────────────────────────────────

    private static List<string> CollectStrings(PropertyValuesEnumerator enumerator)
    {
        var result = new List<string>();
        while (enumerator.MoveNext())
            result.Add(System.Text.Encoding.UTF8.GetString(enumerator.Current.Utf8StringValue));
        return result;
    }
}

[Vertex("TaggedItem")]
internal partial class TaggedItem
{
    [Indexed("idx_taggeditem_label")]
    [Property]
    public string Label { get; set; } = "";

    [Property]
    public int Priority { get; set; }

    [Property]
    public List<string> Tags { get; set; } = [];
}
