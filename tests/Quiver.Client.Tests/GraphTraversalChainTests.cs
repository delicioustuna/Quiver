using FluentAssertions;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>
/// GraphTraversal DSL チェーン構築テスト。Vertices/Out/In/Both/Where/Select/Has/HasLabel
/// 等のステップが正しく連結・実行されることを検証する。
/// </summary>
public sealed class GraphTraversalChainTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public GraphTraversalChainTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_chain_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        var alice = tx.CreateVertex("Person");
        tx.SetProperty(alice, "Name", PropertyValue.FromString("Alice"));
        tx.SetProperty(alice, "Age", PropertyValue.FromInt32(30));

        var bob = tx.CreateVertex("Person");
        tx.SetProperty(bob, "Name", PropertyValue.FromString("Bob"));
        tx.SetProperty(bob, "Age", PropertyValue.FromInt32(25));

        var carol = tx.CreateVertex("Person");
        tx.SetProperty(carol, "Name", PropertyValue.FromString("Carol"));
        tx.SetProperty(carol, "Age", PropertyValue.FromInt32(35));

        var company = tx.CreateVertex("Company");
        tx.SetProperty(company, "Name", PropertyValue.FromString("Acme"));

        tx.CreateEdge(alice, bob, "KNOWS");
        tx.CreateEdge(bob, carol, "KNOWS");
        tx.CreateEdge(alice, company, "WORKS_AT");

        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── Vertices / Edges scan ───────────────────────────────────

    [Fact]
    public void Vertices_returns_all_vertices()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Vertices().ToList();
        all.Should().HaveCount(4);
    }

    [Fact]
    public void Edges_returns_all_edges()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Edges().ToList();
        all.Should().HaveCount(3);
    }

    // ── HasLabel ──────────────────────────────────────────────────────

    [Fact]
    public void HasLabel_filters_by_label()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var persons = g.Vertices().HasLabel("Person").ToList();
        persons.Should().HaveCount(3);
    }

    [Fact]
    public void HasLabel_nonexistent_returns_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var none = g.Vertices().HasLabel("NonExistent").ToList();
        none.Should().BeEmpty();
    }

    // ── Has (property filter) ────────────────────────────────────────

    [Fact]
    public void Has_string_filters_by_property_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", "Alice").ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_int_filters_by_numeric_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Age", 25).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_long_filters_by_numeric_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Age", 30L).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_double_filters_by_numeric_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var wx = _db.BeginTransaction();
        var n = wx.CreateVertex("Measurement");
        wx.SetProperty(n, "Value", PropertyValue.FromDouble(3.14));
        wx.Commit();

        using var tx2 = _db.BeginReadOnlyTransaction();
        var g2 = tx2.G(_db.Schema);
        var result = g2.Vertices().HasLabel("Measurement").Has("Value", 3.14).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_bool_filters_by_boolean_value()
    {
        using var wx = _db.BeginTransaction();
        var n = wx.CreateVertex("Flag");
        wx.SetProperty(n, "Active", PropertyValue.FromBool(true));
        wx.Commit();

        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Flag").Has("Active", true).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_predicate_Gt_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Age", P.Gt(28L)).ToList();
        result.Should().HaveCount(2); // Alice(30), Carol(35)
    }

    [Fact]
    public void Has_predicate_Between_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Age", P.Between(26L, 31L)).ToList();
        result.Should().ContainSingle(); // Alice(30)
    }

    // ── Has / HasNot (存在確認) ──────────────────────────────────────

    [Fact]
    public void Has_key_existence_check()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var withAge = g.Vertices().HasLabel("Person").Has("Age").ToList();
        withAge.Should().HaveCount(3);
    }

    [Fact]
    public void HasNot_key_absence_check()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var withoutAge = g.Vertices().HasLabel("Person").HasNot("NonExistentProp").ToList();
        withoutAge.Should().HaveCount(3);
    }

    [Fact]
    public void IsNull_is_alias_for_HasNot()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").IsNull("Missing").ToList();
        result.Should().HaveCount(3);
    }

    [Fact]
    public void IsNotNull_is_alias_for_Has()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").IsNotNull("Name").ToList();
        result.Should().HaveCount(3);
    }

    // ── Out / In / Both ──────────────────────────────────────────────

    [Fact]
    public void Out_traverses_outgoing_edges()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Alice → KNOWS → Bob
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var neighbors = g.Vertex(alice).Out("KNOWS").ToList();
        neighbors.Should().ContainSingle();
    }

    [Fact]
    public void In_traverses_incoming_edges()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Bob ← KNOWS ← Alice
        var bob = g.Vertices().HasLabel("Person").Has("Name", "Bob").Next();
        var predecessors = g.Vertex(bob).In("KNOWS").ToList();
        predecessors.Should().ContainSingle();
    }

    [Fact]
    public void Both_traverses_bidirectional_edges()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Bob には Alice からの KNOWS と Carol への KNOWS がある
        var bob = g.Vertices().HasLabel("Person").Has("Name", "Bob").Next();
        var both = g.Vertex(bob).Both("KNOWS").ToList();
        both.Should().HaveCount(2);
    }

    [Fact]
    public void Out_without_type_traverses_all_types()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Alice には Bob への KNOWS と Acme への WORKS_AT がある
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var all = g.Vertex(alice).Out().ToList();
        all.Should().HaveCount(2);
    }

    // ── OutEdges / InEdges / BothEdges ────────

    [Fact]
    public void OutEdges_returns_edge_ids()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var edges = g.Vertex(alice).OutEdges("KNOWS").ToList();
        edges.Should().ContainSingle();
    }

    [Fact]
    public void SourceVertex_resolves_edge_to_source()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var source = g.Vertex(alice).OutEdges("KNOWS").SourceVertex().ToList();
        source.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void TargetVertex_resolves_edge_to_target()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var bob = g.Vertices().HasLabel("Person").Has("Name", "Bob").Next();
        var target = g.Vertex(alice).OutEdges("KNOWS").TargetVertex().ToList();
        target.Should().ContainSingle().Which.Should().Be(bob);
    }

    // ── Where (sub-traversal) ────────────────────────────────────────

    [Fact]
    public void Where_filters_by_subtraversal()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // outgoing KNOWS edge を持つ Person
        var result = g.Vertices().HasLabel("Person")
            .Where(t => t.Out("KNOWS"))
            .ToList();
        result.Should().HaveCount(2); // Alice, Bob
    }

    [Fact]
    public void Not_excludes_subtraversal_match()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // outgoing KNOWS edge を持たない Person
        var result = g.Vertices().HasLabel("Person")
            .Not(t => t.Out("KNOWS"))
            .ToList();
        result.Should().ContainSingle(); // Carol
    }

    // ── And / Or (sub-traversal 合成) ────────────────────────────────

    [Fact]
    public void And_requires_all_subtraversals()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // outgoing KNOWS と outgoing WORKS_AT の両方を持つ Person
        var result = g.Vertices().HasLabel("Person")
            .And(
                t => t.Out("KNOWS"),
                t => t.Out("WORKS_AT"))
            .ToList();
        result.Should().ContainSingle(); // Alice only
    }

    [Fact]
    public void Or_requires_any_subtraversal()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // outgoing KNOWS または outgoing WORKS_AT を持つ Person
        var result = g.Vertices().HasLabel("Person")
            .Or(
                t => t.Out("KNOWS"),
                t => t.Out("WORKS_AT"))
            .ToList();
        result.Should().HaveCount(2); // Alice, Bob
    }

    // ── 複数 step の chain ───────────────────────────────────────────

    [Fact]
    public void Multi_step_chain_out_out()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Alice → KNOWS → Bob → KNOWS → Carol
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var twoHop = g.Vertex(alice).Out("KNOWS").Out("KNOWS").ToList();
        twoHop.Should().ContainSingle();
    }

    [Fact]
    public void Chain_HasLabel_then_Has_then_Out()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", "Alice").Out("KNOWS").ToList();
        result.Should().ContainSingle();
    }

    // ── Limit / Skip / Range ─────────────────────────────────────────

    [Fact]
    public void Limit_restricts_result_count()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Limit(2).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Skip_skips_leading_results()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Vertices().HasLabel("Person").ToList();
        var skipped = g.Vertices().HasLabel("Person").Skip(1).ToList();
        skipped.Should().HaveCount(all.Count - 1);
    }

    [Fact]
    public void Range_returns_window()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Range(0, 2).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Limit_negative_throws()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Vertices().Limit(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Count / HasNext ──────────────────────────────────────────────

    [Fact]
    public void Count_returns_correct_count()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Vertices().HasLabel("Person").Count().Should().Be(3);
    }

    [Fact]
    public void HasNext_returns_true_when_results_exist()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Vertices().HasLabel("Person").HasNext().Should().BeTrue();
    }

    [Fact]
    public void HasNext_returns_false_when_no_results()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Vertices().HasLabel("Ghost").HasNext().Should().BeFalse();
    }

    // ── Next / TryNext ───────────────────────────────────────────────

    [Fact]
    public void Next_returns_first_element()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var id = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        id.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Next_throws_on_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Vertices().HasLabel("Ghost").Next();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryNext_returns_default_on_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Ghost").TryNext();
        result.Should().Be(default(VertexId));
    }

    // ── Label / Id step ──────────────────────────────────────────────

    [Fact]
    public void Label_returns_label_names()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var labels = g.Vertices().HasLabel("Person").Label().ToList();
        labels.Should().HaveCount(3);
        labels.Should().AllBe("Person");
    }

    [Fact]
    public void Id_returns_entity_ids()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var ids = g.Vertices().HasLabel("Person").Id().ToList();
        ids.Should().HaveCount(3);
        ids.Should().OnlyContain(id => id > 0);
    }

    // ── Values ───────────────────────────────────────────────────────

    [Fact]
    public void Values_extracts_property_values()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var names = g.Vertices().HasLabel("Person").Values("Name").ToList();
        names.Should().BeEquivalentTo("Alice", "Bob", "Carol");
    }

    // ── OrderBy / Dedup ──────────────────────────────────────────────

    [Fact]
    public void OrderBy_sorts_ascending()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var names = g.Vertices().HasLabel("Person").OrderBy("Name").Values("Name").ToList();
        names.Should().BeInAscendingOrder();
    }

    [Fact]
    public void OrderByDescending_sorts_descending()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var names = g.Vertices().HasLabel("Person").OrderByDescending("Name").Values("Name").ToList();
        names.Should().BeInDescendingOrder();
    }

    [Fact]
    public void Dedup_removes_duplicates()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Alice.Out(KNOWS) = Bob、Bob.Out(KNOWS) = Carol。
        // 両方を合わせても一意なVertexを返す。
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var result = g.Vertex(alice).Out("KNOWS").Both("KNOWS").Dedup().ToList();
        result.Should().OnlyHaveUniqueItems();
    }

    // ── As / Select ──────────────────────────────────────────────────

    [Fact]
    public void As_Select_pins_and_retrieves_entity()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var selected = g.Vertex(alice).As("a").Out("KNOWS").Select("a").ToList();
        selected.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void Select_undefined_alias_throws()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Vertices().HasLabel("Person").Select("undefined");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Select_projection_returns_tuples()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var pairs = g.Vertices().HasLabel("Person").Has("Name", "Alice")
            .As("a").Out("KNOWS").As("b")
            .Select(t => (Source: t.Vertex("a"), Target: t.Vertex("b")));
        pairs.Should().ContainSingle();
        var pair = pairs[0];
        pair.Source.IsValid.Should().BeTrue();
        pair.Target.IsValid.Should().BeTrue();
    }

    // ── Fold / AsEnumerable ──────────────────────────────────────────

    [Fact]
    public void Fold_is_alias_for_ToList()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var list = g.Vertices().HasLabel("Person").Fold();
        var toList = g.Vertices().HasLabel("Person").ToList();
        list.Should().BeEquivalentTo(toList);
    }

    [Fact]
    public void AsEnumerable_yields_all_elements()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var enumerable = g.Vertices().HasLabel("Person").AsEnumerable();
        enumerable.Count().Should().Be(3);
    }

    // ── Repeat ───────────────────────────────────────────────────────

    [Fact]
    public void Repeat_fixed_times()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        // Alice -KNOWS→ Bob -KNOWS→ Carol (2 hop)
        var result = g.Vertex(alice).Repeat(s => s.Out("KNOWS"), times: 2).ToList();
        result.Should().ContainSingle(); // Carol
    }

    [Fact]
    public void Repeat_with_emit_includes_intermediate()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var result = g.Vertex(alice).Repeat(s => s.Out("KNOWS"), times: 2, emit: true).ToList();
        result.Should().HaveCountGreaterThanOrEqualTo(2); // Bob + Carol at minimum
    }

    [Fact]
    public void Repeat_zero_times_throws()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Vertices().Repeat(s => s.Out(), times: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── 集約 (Sum/Max/Min/Mean/GroupCount) ───────────────────────────

    [Fact]
    public void Sum_aggregates_numeric_property()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var sum = g.Vertices().HasLabel("Person").Sum("Age");
        sum.Should().Be(90.0); // 30 + 25 + 35
    }

    [Fact]
    public void Max_returns_maximum()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Vertices().HasLabel("Person").Max("Age").Should().Be(35.0);
    }

    [Fact]
    public void Min_returns_minimum()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Vertices().HasLabel("Person").Min("Age").Should().Be(25.0);
    }

    [Fact]
    public void Mean_returns_average()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Vertices().HasLabel("Person").Mean("Age").Should().Be(30.0);
    }

    [Fact]
    public void GroupCount_groups_by_string_property()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var counts = g.Vertices().HasLabel("Person").GroupCount("Name");
        counts.Should().HaveCount(3);
        counts["Alice"].Should().Be(1);
    }

    // ── P predicate (text) ───────────────────────────────────────────

    [Fact]
    public void P_StartsWith_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", P.StartsWith("Al")).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void P_EndsWith_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", P.EndsWith("ob")).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void P_Contains_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", P.Contains("lic")).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void P_Within_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", P.Within("Alice", "Bob")).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void P_Without_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", P.Without("Alice")).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void P_Regex_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", P.Regex("^[AB]")).ToList();
        result.Should().HaveCount(2); // Alice, Bob
    }

    [Fact]
    public void P_Not_inverts_predicate()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person").Has("Name", P.Not(P.Eq("Alice"))).ToList();
        result.Should().HaveCount(2); // Bob, Carol
    }

    [Fact]
    public void P_And_combines_predicates()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person")
            .Has("Age", P.And(P.Gte(25L), P.Lte(30L)))
            .ToList();
        result.Should().HaveCount(2); // Alice(30), Bob(25)
    }

    [Fact]
    public void P_Or_combines_predicates()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Vertices().HasLabel("Person")
            .Has("Name", P.Or(P.Eq("Alice"), P.Eq("Carol")))
            .ToList();
        result.Should().HaveCount(2);
    }

    // ── Vertex(id) seed ────────────────────────────────────────────────

    [Fact]
    public void Vertex_single_id_seed()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var result = g.Vertex(alice).ToList();
        result.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void Vertices_multiple_id_seed()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Vertices().HasLabel("Person").ToList();
        var result = g.Vertices(all.ToArray()).ToList();
        result.Should().HaveCount(all.Count);
    }

    // ── Union / Coalesce / Optional ──────────────────────────────────

    [Fact]
    public void Union_concatenates_branches()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var result = g.Vertex(alice).Union(
            t => t.Out("KNOWS"),
            t => t.Out("WORKS_AT")
        ).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Coalesce_returns_first_matching_branch()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var carol = g.Vertices().HasLabel("Person").Has("Name", "Carol").Next();
        // Carol に KNOWS edge はないが、この結果も有効
        var result = g.Vertex(carol).Coalesce(
            t => t.Out("KNOWS"),    // empty for Carol
            t => t.In("KNOWS")     // Bob -> Carol
        ).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Optional_passes_through_if_no_match()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var carol = g.Vertices().HasLabel("Person").Has("Name", "Carol").Next();
        var result = g.Vertex(carol).Optional(t => t.Out("KNOWS")).ToList();
        result.Should().ContainSingle().Which.Should().Be(carol);
    }

    // ── ShortestPathTo ───────────────────────────────────────────────

    [Fact]
    public void ShortestPathTo_returns_hop_count()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Vertices().HasLabel("Person").Has("Name", "Alice").Next();
        var carol = g.Vertices().HasLabel("Person").Has("Name", "Carol").Next();
        var dist = g.Vertex(alice).ShortestPathTo(carol, direction: Direction.Outgoing, type: "KNOWS").ToList();
        dist.Should().ContainSingle().Which.Should().Be(2);
    }
}
