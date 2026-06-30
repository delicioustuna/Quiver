using FluentAssertions;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>
/// GraphTraversal DSL チェーン構築テスト。Nodes/Out/In/Both/Where/Select/Has/HasLabel
/// 等のステップが正しく連結・実行されることを検証する。
/// </summary>
public sealed class GraphTraversalChainTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public GraphTraversalChainTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_chain_" + Guid.NewGuid().ToString("N"));
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

        var company = tx.CreateNode("Company");
        tx.SetProperty(company, "Name", PropertyValue.FromString("Acme"));

        tx.CreateRelationship(alice, bob, "KNOWS");
        tx.CreateRelationship(bob, carol, "KNOWS");
        tx.CreateRelationship(alice, company, "WORKS_AT");

        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── Nodes / Relationships scan ───────────────────────────────────

    [Fact]
    public void Nodes_returns_all_nodes()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Nodes().ToList();
        all.Should().HaveCount(4);
    }

    [Fact]
    public void Relationships_returns_all_relationships()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Relationships().ToList();
        all.Should().HaveCount(3);
    }

    // ── HasLabel ──────────────────────────────────────────────────────

    [Fact]
    public void HasLabel_filters_by_label()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var persons = g.Nodes().HasLabel("Person").ToList();
        persons.Should().HaveCount(3);
    }

    [Fact]
    public void HasLabel_nonexistent_returns_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var none = g.Nodes().HasLabel("NonExistent").ToList();
        none.Should().BeEmpty();
    }

    // ── Has (property filter) ────────────────────────────────────────

    [Fact]
    public void Has_string_filters_by_property_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", "Alice").ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_int_filters_by_numeric_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Age", 25).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_long_filters_by_numeric_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Age", 30L).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_double_filters_by_numeric_value()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var wx = _db.BeginTransaction();
        var n = wx.CreateNode("Measurement");
        wx.SetProperty(n, "Value", PropertyValue.FromDouble(3.14));
        wx.Commit();

        using var tx2 = _db.BeginReadOnlyTransaction();
        var g2 = tx2.G(_db.Schema);
        var result = g2.Nodes().HasLabel("Measurement").Has("Value", 3.14).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_bool_filters_by_boolean_value()
    {
        using var wx = _db.BeginTransaction();
        var n = wx.CreateNode("Flag");
        wx.SetProperty(n, "Active", PropertyValue.FromBool(true));
        wx.Commit();

        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Flag").Has("Active", true).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void Has_predicate_Gt_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Age", P.Gt(28L)).ToList();
        result.Should().HaveCount(2); // Alice(30), Carol(35)
    }

    [Fact]
    public void Has_predicate_Between_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Age", P.Between(26L, 31L)).ToList();
        result.Should().ContainSingle(); // Alice(30)
    }

    // ── Has / HasNot (存在確認) ──────────────────────────────────────

    [Fact]
    public void Has_key_existence_check()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var withAge = g.Nodes().HasLabel("Person").Has("Age").ToList();
        withAge.Should().HaveCount(3);
    }

    [Fact]
    public void HasNot_key_absence_check()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var withoutAge = g.Nodes().HasLabel("Person").HasNot("NonExistentProp").ToList();
        withoutAge.Should().HaveCount(3);
    }

    [Fact]
    public void IsNull_is_alias_for_HasNot()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").IsNull("Missing").ToList();
        result.Should().HaveCount(3);
    }

    [Fact]
    public void IsNotNull_is_alias_for_Has()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").IsNotNull("Name").ToList();
        result.Should().HaveCount(3);
    }

    // ── Out / In / Both ──────────────────────────────────────────────

    [Fact]
    public void Out_traverses_outgoing_relationships()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Alice → KNOWS → Bob
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var neighbors = g.Node(alice).Out("KNOWS").ToList();
        neighbors.Should().ContainSingle();
    }

    [Fact]
    public void In_traverses_incoming_relationships()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Bob ← KNOWS ← Alice
        var bob = g.Nodes().HasLabel("Person").Has("Name", "Bob").Next();
        var predecessors = g.Node(bob).In("KNOWS").ToList();
        predecessors.Should().ContainSingle();
    }

    [Fact]
    public void Both_traverses_bidirectional_relationships()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Bob には Alice からの KNOWS と Carol への KNOWS がある
        var bob = g.Nodes().HasLabel("Person").Has("Name", "Bob").Next();
        var both = g.Node(bob).Both("KNOWS").ToList();
        both.Should().HaveCount(2);
    }

    [Fact]
    public void Out_without_type_traverses_all_types()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Alice には Bob への KNOWS と Acme への WORKS_AT がある
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var all = g.Node(alice).Out().ToList();
        all.Should().HaveCount(2);
    }

    // ── OutRelationships / InRelationships / BothRelationships ────────

    [Fact]
    public void OutRelationships_returns_edge_ids()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var rels = g.Node(alice).OutRelationships("KNOWS").ToList();
        rels.Should().ContainSingle();
    }

    [Fact]
    public void SourceNode_resolves_edge_to_source()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var source = g.Node(alice).OutRelationships("KNOWS").SourceNode().ToList();
        source.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void TargetNode_resolves_edge_to_target()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var bob = g.Nodes().HasLabel("Person").Has("Name", "Bob").Next();
        var target = g.Node(alice).OutRelationships("KNOWS").TargetNode().ToList();
        target.Should().ContainSingle().Which.Should().Be(bob);
    }

    // ── Where (sub-traversal) ────────────────────────────────────────

    [Fact]
    public void Where_filters_by_subtraversal()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // outgoing KNOWS edge を持つ Person
        var result = g.Nodes().HasLabel("Person")
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
        var result = g.Nodes().HasLabel("Person")
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
        var result = g.Nodes().HasLabel("Person")
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
        var result = g.Nodes().HasLabel("Person")
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
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var twoHop = g.Node(alice).Out("KNOWS").Out("KNOWS").ToList();
        twoHop.Should().ContainSingle();
    }

    [Fact]
    public void Chain_HasLabel_then_Has_then_Out()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", "Alice").Out("KNOWS").ToList();
        result.Should().ContainSingle();
    }

    // ── Limit / Skip / Range ─────────────────────────────────────────

    [Fact]
    public void Limit_restricts_result_count()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Limit(2).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Skip_skips_leading_results()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Nodes().HasLabel("Person").ToList();
        var skipped = g.Nodes().HasLabel("Person").Skip(1).ToList();
        skipped.Should().HaveCount(all.Count - 1);
    }

    [Fact]
    public void Range_returns_window()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Range(0, 2).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Limit_negative_throws()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().Limit(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Count / HasNext ──────────────────────────────────────────────

    [Fact]
    public void Count_returns_correct_count()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Person").Count().Should().Be(3);
    }

    [Fact]
    public void HasNext_returns_true_when_results_exist()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Person").HasNext().Should().BeTrue();
    }

    [Fact]
    public void HasNext_returns_false_when_no_results()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Ghost").HasNext().Should().BeFalse();
    }

    // ── Next / TryNext ───────────────────────────────────────────────

    [Fact]
    public void Next_returns_first_element()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var id = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        id.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Next_throws_on_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().HasLabel("Ghost").Next();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryNext_returns_default_on_empty()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Ghost").TryNext();
        result.Should().Be(default(NodeId));
    }

    // ── Label / Id step ──────────────────────────────────────────────

    [Fact]
    public void Label_returns_label_names()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var labels = g.Nodes().HasLabel("Person").Label().ToList();
        labels.Should().HaveCount(3);
        labels.Should().AllBe("Person");
    }

    [Fact]
    public void Id_returns_entity_ids()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var ids = g.Nodes().HasLabel("Person").Id().ToList();
        ids.Should().HaveCount(3);
        ids.Should().OnlyContain(id => id > 0);
    }

    // ── Values ───────────────────────────────────────────────────────

    [Fact]
    public void Values_extracts_property_values()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var names = g.Nodes().HasLabel("Person").Values("Name").ToList();
        names.Should().BeEquivalentTo("Alice", "Bob", "Carol");
    }

    // ── OrderBy / Dedup ──────────────────────────────────────────────

    [Fact]
    public void OrderBy_sorts_ascending()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var names = g.Nodes().HasLabel("Person").OrderBy("Name").Values("Name").ToList();
        names.Should().BeInAscendingOrder();
    }

    [Fact]
    public void OrderByDescending_sorts_descending()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var names = g.Nodes().HasLabel("Person").OrderByDescending("Name").Values("Name").ToList();
        names.Should().BeInDescendingOrder();
    }

    [Fact]
    public void Dedup_removes_duplicates()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        // Alice.Out(KNOWS) = Bob、Bob.Out(KNOWS) = Carol。
        // 両方を合わせても一意なノードを返す。
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var result = g.Node(alice).Out("KNOWS").Both("KNOWS").Dedup().ToList();
        result.Should().OnlyHaveUniqueItems();
    }

    // ── As / Select ──────────────────────────────────────────────────

    [Fact]
    public void As_Select_pins_and_retrieves_entity()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var selected = g.Node(alice).As("a").Out("KNOWS").Select("a").ToList();
        selected.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void Select_undefined_alias_throws()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().HasLabel("Person").Select("undefined");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Select_projection_returns_tuples()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var pairs = g.Nodes().HasLabel("Person").Has("Name", "Alice")
            .As("a").Out("KNOWS").As("b")
            .Select(t => (Source: t.Node("a"), Target: t.Node("b")));
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
        var list = g.Nodes().HasLabel("Person").Fold();
        var toList = g.Nodes().HasLabel("Person").ToList();
        list.Should().BeEquivalentTo(toList);
    }

    [Fact]
    public void AsEnumerable_yields_all_elements()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var enumerable = g.Nodes().HasLabel("Person").AsEnumerable();
        enumerable.Count().Should().Be(3);
    }

    // ── Repeat ───────────────────────────────────────────────────────

    [Fact]
    public void Repeat_fixed_times()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        // Alice -KNOWS→ Bob -KNOWS→ Carol (2 hop)
        var result = g.Node(alice).Repeat(s => s.Out("KNOWS"), times: 2).ToList();
        result.Should().ContainSingle(); // Carol
    }

    [Fact]
    public void Repeat_with_emit_includes_intermediate()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var result = g.Node(alice).Repeat(s => s.Out("KNOWS"), times: 2, emit: true).ToList();
        result.Should().HaveCountGreaterThanOrEqualTo(2); // Bob + Carol at minimum
    }

    [Fact]
    public void Repeat_zero_times_throws()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var act = () => g.Nodes().Repeat(s => s.Out(), times: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── 集約 (Sum/Max/Min/Mean/GroupCount) ───────────────────────────

    [Fact]
    public void Sum_aggregates_numeric_property()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var sum = g.Nodes().HasLabel("Person").Sum("Age");
        sum.Should().Be(90.0); // 30 + 25 + 35
    }

    [Fact]
    public void Max_returns_maximum()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Person").Max("Age").Should().Be(35.0);
    }

    [Fact]
    public void Min_returns_minimum()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Person").Min("Age").Should().Be(25.0);
    }

    [Fact]
    public void Mean_returns_average()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        g.Nodes().HasLabel("Person").Mean("Age").Should().Be(30.0);
    }

    [Fact]
    public void GroupCount_groups_by_string_property()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var counts = g.Nodes().HasLabel("Person").GroupCount("Name");
        counts.Should().HaveCount(3);
        counts["Alice"].Should().Be(1);
    }

    // ── P predicate (text) ───────────────────────────────────────────

    [Fact]
    public void P_StartsWith_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", P.StartsWith("Al")).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void P_EndsWith_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", P.EndsWith("ob")).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void P_Contains_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", P.Contains("lic")).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void P_Within_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", P.Within("Alice", "Bob")).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void P_Without_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", P.Without("Alice")).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void P_Regex_filters_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", P.Regex("^[AB]")).ToList();
        result.Should().HaveCount(2); // Alice, Bob
    }

    [Fact]
    public void P_Not_inverts_predicate()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").Has("Name", P.Not(P.Eq("Alice"))).ToList();
        result.Should().HaveCount(2); // Bob, Carol
    }

    [Fact]
    public void P_And_combines_predicates()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person")
            .Has("Age", P.And(P.Gte(25L), P.Lte(30L)))
            .ToList();
        result.Should().HaveCount(2); // Alice(30), Bob(25)
    }

    [Fact]
    public void P_Or_combines_predicates()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person")
            .Has("Name", P.Or(P.Eq("Alice"), P.Eq("Carol")))
            .ToList();
        result.Should().HaveCount(2);
    }

    // ── Node(id) seed ────────────────────────────────────────────────

    [Fact]
    public void Node_single_id_seed()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var result = g.Node(alice).ToList();
        result.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void Nodes_multiple_id_seed()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var all = g.Nodes().HasLabel("Person").ToList();
        var result = g.Nodes(all.ToArray()).ToList();
        result.Should().HaveCount(all.Count);
    }

    // ── Union / Coalesce / Optional ──────────────────────────────────

    [Fact]
    public void Union_concatenates_branches()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var result = g.Node(alice).Union(
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
        var carol = g.Nodes().HasLabel("Person").Has("Name", "Carol").Next();
        // Carol に KNOWS edge はないが、この結果も有効
        var result = g.Node(carol).Coalesce(
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
        var carol = g.Nodes().HasLabel("Person").Has("Name", "Carol").Next();
        var result = g.Node(carol).Optional(t => t.Out("KNOWS")).ToList();
        result.Should().ContainSingle().Which.Should().Be(carol);
    }

    // ── ShortestPathTo ───────────────────────────────────────────────

    [Fact]
    public void ShortestPathTo_returns_hop_count()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);
        var alice = g.Nodes().HasLabel("Person").Has("Name", "Alice").Next();
        var carol = g.Nodes().HasLabel("Person").Has("Name", "Carol").Next();
        var dist = g.Node(alice).ShortestPathTo(carol, direction: Direction.Outgoing, type: "KNOWS").ToList();
        dist.Should().ContainSingle().Which.Should().Be(2);
    }
}
