using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class GraphDatabaseTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public GraphDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_test_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ===== Node CRUD =====

    [Fact]
    public void CreateNode_and_NodeExists()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("Person");
        tx.NodeExists(id).Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void DeleteNode_makes_it_disappear()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("Person");
        tx.DeleteNode(id);
        tx.NodeExists(id).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void DeleteNode_with_relationships_succeeds()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        tx.CreateRelationship(a, b, "KNOWS");
        tx.DeleteNode(a);
        tx.NodeExists(a).Should().BeFalse();
        tx.Commit();
    }

    // ===== Relationship CRUD =====

    [Fact]
    public void CreateRelationship_and_enumerate()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");

        var neighbors = new List<NodeId>();
        var en = tx.EnumerateRelationships(alice);
        while (en.MoveNext())
        {
            var rel = en.Current;
            neighbors.Add(rel.Source == alice ? rel.Target : rel.Source);
        }

        neighbors.Should().ContainSingle().Which.Should().Be(bob);
        tx.Commit();
    }

    [Fact]
    public void DeleteRelationship_removes_it_from_enumeration()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");
        tx.DeleteRelationship(rel);

        var en = tx.EnumerateRelationships(a);
        bool any = en.MoveNext();
        any.Should().BeFalse();
        tx.Commit();
    }

    // ===== Properties =====

    [Fact]
    public void SetProperty_Int64_and_GetProperty()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("Item");
        tx.SetProperty(id, "score", PropertyValue.FromInt64(42L));
        var val = tx.GetProperty(id, "score");
        val.Type.Should().Be(PropertyValueType.Int64);
        val.Int64Value.Should().Be(42L);
        tx.Commit();
    }

    [Fact]
    public void SetProperty_String_and_GetProperty()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString("Alice"));
        var val = tx.GetProperty(id, "name");
        val.Type.Should().Be(PropertyValueType.String);
        System.Text.Encoding.UTF8.GetString(val.Utf8StringValue).Should().Be("Alice");
        tx.Commit();
    }

    [Fact]
    public void HasProperty_returns_correct_values()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("X");
        tx.SetProperty(id, "exists", PropertyValue.FromBool(true));
        tx.HasProperty(id, "exists").Should().BeTrue();
        tx.HasProperty(id, "missing").Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void RemoveProperty_removes_it()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("X");
        tx.SetProperty(id, "age", PropertyValue.FromInt32(30));
        tx.RemoveProperty(id, "age");
        tx.HasProperty(id, "age").Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void SetProperty_overwrites_previous_value()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("X");
        tx.SetProperty(id, "n", PropertyValue.FromInt64(1L));
        tx.SetProperty(id, "n", PropertyValue.FromInt64(99L));
        tx.GetProperty(id, "n").Int64Value.Should().Be(99L);
        tx.Commit();
    }

    // ===== Operators via Execute =====

    [Fact]
    public void AllNodesScan_returns_all_nodes()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("X");
        var b = tx.CreateNode("X");
        var c = tx.CreateNode("X");

        var scan = new AllNodesScanOperator();
        using var result = tx.Execute(scan);
        var ids = result.Rows().Select(r => r.GetNodeId(0)).ToList();
        ids.Should().Contain([a, b, c]);
        tx.Rollback();
    }

    [Fact]
    public void AllNodesScan_with_label_filter()
    {
        using var tx = _db.BeginTransaction();
        tx.CreateNode("Person");
        tx.CreateNode("Person");
        tx.CreateNode("Car");

        var personLabel = _db.Schema.GetOrCreateLabel("Person");
        var scan = new AllNodesScanOperator(personLabel);
        using var result = tx.Execute(scan);
        result.Rows().Should().HaveCount(2);
        tx.Rollback();
    }

    [Fact]
    public void LimitOperator_via_Execute_limits_rows()
    {
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < 5; i++) tx.CreateNode("N");

        var scan = new AllNodesScanOperator();
        var limit = new LimitOperator(scan, limit: 3);
        using var result = tx.Execute(limit);
        result.Rows().Should().HaveCount(3);
        tx.Rollback();
    }

    [Fact]
    public void PropertyLookup_via_Execute_returns_string_value()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString("Bob"));

        var nameKey = _db.Schema.GetOrCreatePropertyKey("name");
        var scan = new AllNodesScanOperator();
        var lookup = new PropertyLookupOperator(scan, entityIdColumn: 0, nameKey, "name");
        using var result = tx.Execute(lookup);

        var rows = result.Rows().ToList();
        rows.Should().HaveCount(1);
        rows[0].GetString(1).Should().Be("Bob");
        tx.Rollback();
    }

    [Fact]
    public void ExpandOperator_via_Execute_finds_neighbor()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");

        var source = new NodeByLabelScanOperator(_db.Schema.GetOrCreateLabel("Person"));
        var expand = new ExpandOperator(source, sourceNodeColumn: 0,
            Direction.Outgoing, typeFilter: null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(expand);

        var neighbors = result.Rows().Select(r => r.GetNodeId(0)).ToList();
        neighbors.Should().Contain(bob);
        tx.Rollback();
    }

    // ===== Relationship Properties =====

    [Fact]
    public void SetProperty_on_relationship_and_GetProperty()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var rel   = tx.CreateRelationship(alice, bob, "KNOWS");

        tx.SetProperty(rel, "since", PropertyValue.FromInt64(2020L));
        var val = tx.GetProperty(rel, "since");

        val.Type.Should().Be(PropertyValueType.Int64);
        val.Int64Value.Should().Be(2020L);
        tx.Commit();
    }

    [Fact]
    public void SetProperty_on_relationship_overwrites_previous_value()
    {
        using var tx = _db.BeginTransaction();
        var a   = tx.CreateNode("A");
        var b   = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");

        tx.SetProperty(rel, "weight", PropertyValue.FromDouble(1.0));
        tx.SetProperty(rel, "weight", PropertyValue.FromDouble(9.9));
        var val = tx.GetProperty(rel, "weight");

        val.Type.Should().Be(PropertyValueType.Double);
        val.DoubleValue.Should().BeApproximately(9.9, 1e-9);
        tx.Commit();
    }

    [Fact]
    public void GetProperty_on_relationship_returns_default_for_missing_key()
    {
        using var tx = _db.BeginTransaction();
        var a   = tx.CreateNode("A");
        var b   = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");

        var val = tx.GetProperty(rel, "nonexistent");
        val.Type.Should().Be(default(PropertyValueType));
        tx.Rollback();
    }

    [Fact]
    public void DeleteRelationship_frees_its_properties()
    {
        using var tx = _db.BeginTransaction();
        var a   = tx.CreateNode("A");
        var b   = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");
        tx.SetProperty(rel, "since", PropertyValue.FromInt64(2021L));

        tx.DeleteRelationship(rel);

        var en = tx.EnumerateRelationships(a);
        en.MoveNext().Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void DeleteNode_with_relationship_properties_succeeds()
    {
        using var tx = _db.BeginTransaction();
        var a   = tx.CreateNode("A");
        var b   = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");
        tx.SetProperty(rel, "x", PropertyValue.FromInt32(42));

        tx.DeleteNode(a);
        tx.NodeExists(a).Should().BeFalse();
        tx.Commit();
    }

    // ===== 型付き Traversal API =====

    [Fact]
    public void Relationship_generation_lookup_survives_reopen_and_adjacency_compact()
    {
        var path = Path.Combine(_dir, "relationship_locator_reopen.quiver");
        RelationshipId stamped;

        using (var db = GraphDatabase.Open(path))
        {
            using (var tx = db.BeginTransaction())
            {
                var a = tx.CreateNode("A");
                var b = tx.CreateNode("B");
                var rel = tx.CreateRelationship(a, b, "LINK");
                stamped = RelationshipId.Create(rel.Sequence, 1);
                tx.SetProperty(rel, "weight", PropertyValue.FromInt64(10));
                tx.Commit();
            }

            db.CompactAdjacency();
        }

        using (var db = GraphDatabase.Open(path))
        {
            using var tx = db.BeginReadOnlyTransaction();
            var value = tx.GetProperty(stamped, "weight");
            value.Type.Should().Be(PropertyValueType.Int64);
            value.Int64Value.Should().Be(10);
        }

        using (var db = GraphDatabase.Open(path))
        {
            db.CompactAdjacency();
            using var tx = db.BeginReadOnlyTransaction();
            var value = tx.GetProperty(stamped, "weight");
            value.Type.Should().Be(PropertyValueType.Int64);
            value.Int64Value.Should().Be(10);
        }
    }

    [Fact]
    public void Stale_relationship_generation_does_not_resolve_after_sequence_reuse()
    {
        var path = Path.Combine(_dir, "relationship_locator_reuse.quiver");
        using var db = GraphDatabase.Open(path);

        NodeId a;
        NodeId b;
        NodeId c;
        RelationshipId old;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("A");
            b = tx.CreateNode("B");
            c = tx.CreateNode("C");
            old = tx.CreateRelationship(a, b, "LINK");
            tx.SetProperty(old, "weight", PropertyValue.FromInt64(1));
            tx.Commit();
        }

        var stale = RelationshipId.Create(old.Sequence, 1);
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteRelationship(old);
            tx.Commit();
        }

        db.Vacuum().ReclaimedRelationships.Should().Be(1);

        RelationshipId reused;
        using (var tx = db.BeginTransaction())
        {
            reused = tx.CreateRelationship(a, c, "LINK");
            tx.SetProperty(reused, "weight", PropertyValue.FromInt64(2));
            tx.Commit();
        }

        reused.Sequence.Should().Be(old.Sequence);
        var current = RelationshipId.Create(reused.Sequence, 2);
        db.CompactAdjacency();

        using (var tx = db.BeginTransaction())
        {
            tx.GetProperty(stale, "weight").Type.Should().Be(default(PropertyValueType));
            tx.SetProperty(stale, "weight", PropertyValue.FromInt64(99));
            tx.DeleteRelationship(stale);
            tx.Commit();
        }

        using (var tx = db.BeginReadOnlyTransaction())
        {
            tx.GetProperty(current, "weight").Int64Value.Should().Be(2);
            tx.GetProperty(new RelationshipId(reused.Sequence), "weight").Int64Value.Should().Be(2);

            var targets = new List<NodeId>();
            var rels = tx.EnumerateRelationships(a, Direction.Outgoing);
            while (rels.MoveNext())
                targets.Add(rels.Current.Target);

            targets.Should().ContainSingle().Which.Should().Be(c);
        }
    }

    private partial class KnowsRel : IGraphRelationship<KnowsRel, PersonNode, PersonNode>
    {
        [Property]
        public int Since { get; set; }

        public static string GraphType => "KNOWS";
        public static RelationshipId Insert(IGraphTransaction tx, NodeId from, NodeId to, KnowsRel entity)
        {
            var id = tx.CreateRelationship(from, to, "KNOWS");
            tx.SetProperty(id, "Since", PropertyValue.FromInt32(entity.Since));
            return id;
        }
        public static KnowsRel Load(IGraphTransaction tx, RelationshipId id)
            => new() { Since = tx.GetProperty(id, "Since").Int32Value };
        public static void Update(IGraphTransaction tx, RelationshipId id, KnowsRel entity)
            => tx.SetProperty(id, "Since", PropertyValue.FromInt32(entity.Since));
        public static void Delete(IGraphTransaction tx, RelationshipId id)
            => tx.DeleteRelationship(id);
    }

    private partial class PersonNode : IGraphNode<PersonNode>
    {
        public string Name { get; set; } = "";
        public double Score { get; set; }
        public DateTime CreatedAt { get; set; }

        public static string GraphLabel => "Person";
        public static NodeId Insert(IGraphTransaction tx, PersonNode entity)
        {
            var id = tx.CreateNode("Person");
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Score", PropertyValue.FromDouble(entity.Score));
            tx.SetProperty(id, "CreatedAt", PropertyValue.FromDateTime(entity.CreatedAt));
            return id;
        }
        public static NodeId InsertIndexed(IGraphTransaction tx, PersonNode entity) => Insert(tx, entity);
        public static PersonNode Load(IGraphTransaction tx, NodeId id)
            => new()
            {
                Name = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Name").Utf8StringValue),
                Score = tx.GetProperty(id, "Score").DoubleValue,
                CreatedAt = tx.GetProperty(id, "CreatedAt").DateTimeValue,
            };
        public static void Update(IGraphTransaction tx, NodeId id, PersonNode entity)
            => tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
        public static void Delete(IGraphTransaction tx, NodeId id) => tx.DeleteNode(id);
    }

    [Fact]
    public void TypedTraversal_Out_generic_finds_neighbor()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");

        var g = tx.G(_db.Schema);
        var neighbors = g.Node(alice).Out<KnowsRel>().ToList();

        neighbors.Should().ContainSingle().Which.Should().Be(bob);
        tx.Rollback();
    }

    [Fact]
    public void TypedTraversal_In_generic_finds_neighbor()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");

        var g = tx.G(_db.Schema);
        var neighbors = g.Node(bob).In<KnowsRel>().ToList();

        neighbors.Should().ContainSingle().Which.Should().Be(alice);
        tx.Rollback();
    }

    [Fact]
    public void TypedGraphTraversal_Out_generic_finds_neighbor()
    {
        using var tx = _db.BeginTransaction();
        var alice = PersonNode.Insert(tx, new PersonNode { Name = "Alice" });
        var bob   = PersonNode.Insert(tx, new PersonNode { Name = "Bob" });
        tx.CreateRelationship(alice, bob, "KNOWS");

        var g = tx.G(_db.Schema);
        // Out<TRel, TTarget>() は TypedGraphTraversal<PersonNode> を型保存する。
        var neighbors = g.Nodes<PersonNode>().Out<KnowsRel, PersonNode>().ToListWithIds();

        neighbors.Select(n => n.Id).Should().Contain(bob);
        tx.Rollback();
    }

    // ===== 式ツリー述語 (Where / OutWhere) =====

    [Fact]
    public void TypedGraphTraversal_Where_expression_filters_nodes()
    {
        using var tx = _db.BeginTransaction();
        var alice = PersonNode.Insert(tx, new PersonNode { Name = "Alice" });
        var bob   = PersonNode.Insert(tx, new PersonNode { Name = "Bob" });

        var g = tx.G(_db.Schema);
        var found = g.Nodes<PersonNode>()
                     .Where(p => p.Name == "Alice" || p.Name.StartsWith("Al"))
                     .ToListWithIds();

        found.Select(n => n.Id).Should().Contain(alice).And.NotContain(bob);
        tx.Rollback();
    }

    [Fact]
    public void TypedGraphTraversal_OutWhere_edge_predicate_filters()
    {
        using var tx = _db.BeginTransaction();
        var alice = PersonNode.Insert(tx, new PersonNode { Name = "Alice" });
        var bob   = PersonNode.Insert(tx, new PersonNode { Name = "Bob" });
        var carol = PersonNode.Insert(tx, new PersonNode { Name = "Carol" });
        KnowsRel.Insert(tx, alice, bob,   new KnowsRel { Since = 2020 });
        KnowsRel.Insert(tx, alice, carol, new KnowsRel { Since = 2024 });

        var g = tx.G(_db.Schema);
        // リレーションシッププロパティ Since で絞り込みつつ PersonNode 型を保存して対象へ進む。
        var recent = g.Nodes<PersonNode>()
                      .Where(p => p.Name == "Alice")
                      .OutWhere<KnowsRel, PersonNode>(e => e.Since > 2022)
                      .ToListWithIds();

        recent.Select(n => n.Id).Should().Contain(carol).And.NotContain(bob);
        tx.Rollback();
    }

    // ===== 浮動小数点の範囲述語 =====

    [Fact]
    public void Has_double_range_filters_via_predicate()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("Item"); tx.SetProperty(a, "score", PropertyValue.FromDouble(1.5));
        var b = tx.CreateNode("Item"); tx.SetProperty(b, "score", PropertyValue.FromDouble(2.5));

        var g = tx.G(_db.Schema);
        var hi = g.Nodes().HasLabel("Item").Has("score", P.Gt(2.0)).ToList();

        hi.Should().ContainSingle().Which.Should().Be(b);
        tx.Rollback();
    }

    [Fact]
    public void TypedWhere_double_range_filters()
    {
        using var tx = _db.BeginTransaction();
        var alice = PersonNode.Insert(tx, new PersonNode { Name = "Alice", Score = 1.5 });
        var bob   = PersonNode.Insert(tx, new PersonNode { Name = "Bob",   Score = 2.5 });

        var g = tx.G(_db.Schema);
        // double メンバーの式ツリー比較では、整数リテラルも double 比較へ振り分ける。
        var found = g.Nodes<PersonNode>().Where(p => p.Score > 2).ToListWithIds();

        found.Select(n => n.Id).Should().Contain(bob).And.NotContain(alice);
        tx.Rollback();
    }

    [Fact]
    public void DateTime_roundtrip_canonicalizes_to_utc_instant()
    {
        // Local 入力は UTC の瞬時へ正規化し、Utc Kind として復元する。
        var local = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local);
        var pv = PropertyValue.FromDateTime(local);
        pv.DateTimeValue.Should().Be(local.ToUniversalTime());
        pv.DateTimeValue.Kind.Should().Be(DateTimeKind.Utc);

        // Unspecified は (マシン依存を避けるため) UTC 扱い = ticks そのまま。
        var unspec = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        PropertyValue.FromDateTime(unspec).Int64Value.Should().Be(unspec.Ticks);
    }

    [Fact]
    public void TypedWhere_DateTime_range_filters_with_tz_normalization()
    {
        using var tx = _db.BeginTransaction();
        var utc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var alice = PersonNode.Insert(tx, new PersonNode { Name = "Alice", CreatedAt = utc.AddDays(1) });
        var bob   = PersonNode.Insert(tx, new PersonNode { Name = "Bob",   CreatedAt = utc.AddDays(10) });

        var g = tx.G(_db.Schema);
        // 同一瞬時を Local で渡しても UTC へ正準化され一貫比較される。
        var cutoffLocal = utc.AddDays(5).ToLocalTime();
        var recent = g.Nodes<PersonNode>().Where(p => p.CreatedAt > cutoffLocal).ToListWithIds();

        recent.Select(n => n.Id).Should().Contain(bob).And.NotContain(alice);
        tx.Rollback();
    }

    [Fact]
    public void EdgeTraversal_Has_filters_relationship_properties()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var carol = tx.CreateNode("Person");
        var r1 = tx.CreateRelationship(alice, bob,   "KNOWS");
        var r2 = tx.CreateRelationship(alice, carol, "KNOWS");
        tx.SetProperty(r1, "since", PropertyValue.FromInt64(2020));
        tx.SetProperty(r2, "since", PropertyValue.FromInt64(2024));

        var g = tx.G(_db.Schema);
        // リレーションシップトラバーサルの .Has はリレーションシッププロパティを読む。
        var rels = g.Node(alice).OutRelationships("KNOWS").Has("since", P.Gt(2022L)).ToList();

        rels.Should().ContainSingle().Which.Should().Be(r2);
        tx.Rollback();
    }

    // ===== OutE<TRel> / InE<TRel> / BothE<TRel> =====

    [Fact]
    public void OutE_generic_returns_relationship_id()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var rel   = tx.CreateRelationship(alice, bob, "KNOWS");

        var g    = tx.G(_db.Schema);
        var rels = g.Node(alice).OutRelationships<KnowsRel>().ToList();

        rels.Should().ContainSingle().Which.Should().Be(rel);
        tx.Rollback();
    }

    [Fact]
    public void InE_generic_returns_relationship_id()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var rel   = tx.CreateRelationship(alice, bob, "KNOWS");

        var g    = tx.G(_db.Schema);
        var rels = g.Node(bob).InRelationships<KnowsRel>().ToList();

        rels.Should().ContainSingle().Which.Should().Be(rel);
        tx.Rollback();
    }

    [Fact]
    public void BothE_generic_returns_both_directions()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var rel   = tx.CreateRelationship(alice, bob, "KNOWS");

        var g    = tx.G(_db.Schema);
        var fromAlice = g.Node(alice).BothRelationships<KnowsRel>().ToList();
        var fromBob   = g.Node(bob).BothRelationships<KnowsRel>().ToList();

        fromAlice.Should().ContainSingle().Which.Should().Be(rel);
        fromBob.Should().ContainSingle().Which.Should().Be(rel);
        tx.Rollback();
    }

    [Fact]
    public void TypedTraversal_OutE_generic_returns_relationship_id()
    {
        using var tx = _db.BeginTransaction();
        var alice = PersonNode.Insert(tx, new PersonNode { Name = "Alice" });
        var bob   = PersonNode.Insert(tx, new PersonNode { Name = "Bob" });
        var rel   = KnowsRel.Insert(tx, alice, bob, new KnowsRel { Since = 2020 });

        var g    = tx.G(_db.Schema);
        var rels = g.Nodes<PersonNode>().OutRelationships<KnowsRel>().ToList();

        rels.Should().Contain(rel);
        tx.Rollback();
    }

    // ===== Values<TProp>(expr) =====

    [Fact]
    public void TypedTraversal_Values_expression_returns_property()
    {
        using var tx = _db.BeginTransaction();
        PersonNode.Insert(tx, new PersonNode { Name = "Alice" });
        PersonNode.Insert(tx, new PersonNode { Name = "Bob" });

        var g     = tx.G(_db.Schema);
        var names = g.Nodes<PersonNode>().Values(p => p.Name).ToList();

        names.Should().Contain("Alice").And.Contain("Bob");
        tx.Rollback();
    }

    // ===== SeekIndex / RangeIndex =====

    [Fact]
    public void SeekIndex_string_equality_finds_node()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
        tx.SetProperty(bob,   "name", PropertyValue.FromString("Bob"));
        tx.IndexInsert("idx_name", "Alice", alice);
        tx.IndexInsert("idx_name", "Bob",   bob);

        var key = PropertyValue.FromString("Alice");
        var en = tx.SeekIndex("idx_name", key);
        var results = new List<NodeId>();
        while (en.MoveNext()) results.Add(en.Current);
        en.Dispose();

        results.Should().ContainSingle().Which.Should().Be(alice);
        tx.Rollback();
    }

    [Fact]
    public void SeekIndex_int64_equality_finds_node()
    {
        using var tx = _db.BeginTransaction();
        var n42 = tx.CreateNode("Item");
        var n99 = tx.CreateNode("Item");
        tx.IndexInsert("idx_score", 42L, n42);
        tx.IndexInsert("idx_score", 99L, n99);

        var key = PropertyValue.FromInt64(42L);
        var en = tx.SeekIndex("idx_score", key);
        var results = new List<NodeId>();
        while (en.MoveNext()) results.Add(en.Current);
        en.Dispose();

        results.Should().ContainSingle().Which.Should().Be(n42);
        tx.Rollback();
    }

    [Fact]
    public void RangeIndex_int64_returns_nodes_in_range()
    {
        using var tx = _db.BeginTransaction();
        var n10 = tx.CreateNode("Item");
        var n20 = tx.CreateNode("Item");
        var n30 = tx.CreateNode("Item");
        tx.IndexInsert("idx_val", 10L, n10);
        tx.IndexInsert("idx_val", 20L, n20);
        tx.IndexInsert("idx_val", 30L, n30);

        var from = PropertyValue.FromInt64(10L);
        var to   = PropertyValue.FromInt64(25L);
        var en = tx.RangeIndex("idx_val", from, fromInclusive: true, to, toInclusive: true);
        var results = new List<NodeId>();
        while (en.MoveNext()) results.Add(en.Current);
        en.Dispose();

        results.Should().Contain(n10).And.Contain(n20).And.NotContain(n30);
        tx.Rollback();
    }

    [Fact]
    public void SeekIndex_on_nonexistent_index_returns_empty()
    {
        using var tx = _db.BeginTransaction();
        var key = PropertyValue.FromInt64(1L);
        var en = tx.SeekIndex("no_such_index", key);
        en.MoveNext().Should().BeFalse();
        en.Dispose();
        tx.Rollback();
    }

    // ===== WAL 耐久性（クラッシュシナリオ） =====

    [Fact]
    public void Committed_data_survives_data_file_loss_via_wal_replay()
    {
        // Simulate crash: data files are reverted to pre-write state (buffer not flushed),
        // but WAL is intact. Recovery must restore the committed data.
        var dir = Path.Combine(Path.GetTempPath(), "quiver_crash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 最初にデータベースを作成し、必要なファイルを初期状態で用意する。
            {
                using var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
            }

            // 単一ファイルコンテナでは、すべてのコアデータを graph.quiver に格納する。
            byte[] dataSnap = File.ReadAllBytes(Path.Combine(dir, "graph.quiver"));

            NodeId aliceId;

            // データを書き込んでコミットする。WAL はフラッシュされるが、バッファープールは未反映でもよい。
            // 正常終了時は Dispose が全ページのフラッシュと WAL の削除を行うため、
            // graph.quiver が確定して WAL が消える。WAL replay 経路を検証するため、未コミットの tx を
            // 1 つ開いたまま Dispose して「クラッシュ (ActiveCount>0 → WAL 非削除)」を模擬する。
            {
                var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
                using (var tx = db.BeginTransaction())
                {
                    aliceId = tx.CreateNode("Person");
                    tx.SetProperty(aliceId, "name", PropertyValue.FromString("Alice"));
                    tx.Commit();
                }
                _ = db.BeginTransaction(); // 未コミットのまま放置 → ActiveCount>0 → クリーン終了抑止 → WAL 残存
                db.Dispose();
            }

            // Restore pre-write data file to simulate crash (buffer not written to disk).
            File.WriteAllBytes(Path.Combine(dir, "graph.quiver"), dataSnap);

            // 再オープンし、RecoveryManager が WAL の PageImage レコードを再実行する。
            {
                using var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
                using var tx = db.BeginReadOnlyTransaction();
                tx.NodeExists(aliceId).Should().BeTrue("WAL recovery must restore the committed node");
                tx.Rollback();
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ===== 診断情報 =====

    [Fact]
    public void Diagnostics_exposes_adjacency_fallback_count()
    {
        // Without an adjacency block built, the backend always falls into the
        // linked-list path so the counter never increments — but it must at
        // DatabaseStatistics の読み取り可能なフィールドとして公開されることを確認する。
        var stats = _db.Diagnostics.GetStatistics();
        stats.AdjacencyFallbackCount.Should().BeGreaterOrEqualTo(0);
    }

    // ===== Match DSL 型付き overload =====

    [Fact]
    public void Match_typed_Out_and_Load_returns_entity()
    {
        using var tx = _db.BeginTransaction();
        var alice = PersonNode.Insert(tx, new PersonNode { Name = "Alice" });
        var bob   = PersonNode.Insert(tx, new PersonNode { Name = "Bob" });
        tx.CreateRelationship(alice, bob, "KNOWS");

        var g = tx.G(_db.Schema);
        var p = Quiver.Api.Match.GraphPattern.Node("p", "Person");
        var q = Quiver.Api.Match.GraphPattern.Node("q", "Person");

        var results = g.Match(p.Out<KnowsRel>(q))
                       .Return(ctx => ctx.Load<PersonNode>("q"))
                       .ToList();

        results.Should().ContainSingle().Which.Name.Should().Be("Bob");
        tx.Rollback();
    }

    // ===== ストリーミングカーソル =====

    [Fact]
    public void GraphTraversal_AsEnumerable_streams_without_full_materialise()
    {
        using var tx = _db.BeginTransaction();
        tx.CreateNode("Person");
        tx.CreateNode("Person");
        tx.CreateNode("Person");
        tx.Commit();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        int count = 0;
        foreach (var _ in g.Nodes().HasLabel("Person").AsEnumerable())
            count++;
        rtx.Rollback();

        count.Should().Be(3);
    }

    [Fact]
    public void GraphTraversal_AsCursor_streams_results()
    {
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < 5; i++)
        {
            var id = tx.CreateNode("Counter");
            tx.SetProperty(id, "n", PropertyValue.FromInt64(i));
        }
        tx.Commit();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var names = new List<string>();
        using (var cursor = g.Nodes().HasLabel("Counter").Values("n").AsCursor())
        {
            while (cursor.MoveNext())
                names.Add(cursor.Current);
        }
        rtx.Rollback();

        names.Should().HaveCount(5);
    }

    [Fact]
    public void MatchQuery_AsCursor_streams_results()
    {
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("StreamPerson");
        tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
        var bob = tx.CreateNode("StreamPerson");
        tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));
        tx.CreateRelationship(alice, bob, "STREAM_KNOWS");
        tx.Commit();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);
        var p = Quiver.Api.Match.GraphPattern.Node("p", "StreamPerson");
        var q = Quiver.Api.Match.GraphPattern.Node("q", "StreamPerson");
        var found = new List<string>();
        using (var cursor = g.Match(p.Out("STREAM_KNOWS", q)).Return(ctx => ctx["q"].Get<string>("name")).AsCursor())
        {
            while (cursor.MoveNext())
                found.Add(cursor.Current);
        }
        rtx.Rollback();

        found.Should().ContainSingle().Which.Should().Be("Bob");
    }

    // ===== サブトラバーサル述語 =====

    [Fact]
    public void Where_out_exists_keeps_only_nodes_with_neighbor()
    {
        // alice → bob (KNOWS), charlie has no edges
        using var tx = _db.BeginTransaction();
        var alice   = tx.CreateNode("Person");
        var bob     = tx.CreateNode("Person");
        var charlie = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");
        tx.Commit();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var results = g.Nodes().HasLabel("Person")
                          .Where(t => t.Out("KNOWS"))
                          .ToList();

        results.Should().ContainSingle().Which.Should().Be(alice);
        rtx.Rollback();
    }

    [Fact]
    public void Not_out_exists_keeps_only_nodes_without_neighbor()
    {
        using var tx = _db.BeginTransaction();
        var alice   = tx.CreateNode("Person");
        var bob     = tx.CreateNode("Person");
        var charlie = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob, "KNOWS");
        tx.Commit();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var results = g.Nodes().HasLabel("Person")
                          .Not(t => t.Out("KNOWS"))
                          .ToList();

        results.Should().HaveCount(2);
        results.Should().Contain(bob).And.Contain(charlie);
        results.Should().NotContain(alice);
        rtx.Rollback();
    }

    [Fact]
    public void Where_out_with_property_filter_passes_only_matching_neighbors()
    {
        // alice → bob("name"="Bob"), dave → eve("name"="Eve")
        // Where(t => t.Out("KNOWS").Has("name", "Bob")) should return only alice
        using var tx = _db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var dave  = tx.CreateNode("Person");
        var eve   = tx.CreateNode("Person");
        tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));
        tx.SetProperty(eve, "name", PropertyValue.FromString("Eve"));
        tx.CreateRelationship(alice, bob, "KNOWS");
        tx.CreateRelationship(dave, eve, "KNOWS");
        tx.Commit();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var results = g.Nodes().HasLabel("Person")
                          .Where(t => t.Out("KNOWS").Has("name", "Bob"))
                          .ToList();

        results.Should().ContainSingle().Which.Should().Be(alice);
        rtx.Rollback();
    }

    [Fact]
    public void Where_multiple_nodes_each_probed_independently()
    {
        // 10 nodes, even-indexed ones each have a KNOWS edge
        using var tx = _db.BeginTransaction();
        var nodes = new NodeId[10];
        for (int i = 0; i < 10; i++)
            nodes[i] = tx.CreateNode("Item");
        for (int i = 0; i < 10; i += 2)
        {
            var target = tx.CreateNode("Target");
            tx.CreateRelationship(nodes[i], target, "LINKS");
        }
        tx.Commit();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var results = g.Nodes().HasLabel("Item")
                          .Where(t => t.Out("LINKS"))
                          .ToList();

        results.Should().HaveCount(5);
        rtx.Rollback();
    }

    // ===== EnforceExclusiveWriter =====

    [Fact]
    public void EnforceExclusiveWriter_blocks_second_writer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = GraphDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new GraphDatabaseOptions { EnforceExclusiveWriter = true });

            using var tx1 = db.BeginTransaction();
            var act = () => db.BeginTransaction();
            act.Should().Throw<TransactionException>();
            tx1.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnforceExclusiveWriter_allows_after_commit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = GraphDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new GraphDatabaseOptions { EnforceExclusiveWriter = true });

            using (var tx1 = db.BeginTransaction()) { tx1.Commit(); }
            using var tx2 = db.BeginTransaction();
            tx2.CreateNode("A");
            tx2.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnforceExclusiveWriter_allows_after_rollback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = GraphDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new GraphDatabaseOptions { EnforceExclusiveWriter = true });

            using (var tx1 = db.BeginTransaction()) { tx1.Rollback(); }
            using var tx2 = db.BeginTransaction();
            tx2.CreateNode("A");
            tx2.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnforceExclusiveWriter_allows_after_dispose_without_commit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = GraphDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new GraphDatabaseOptions { EnforceExclusiveWriter = true });

            using (db.BeginTransaction()) { /* dispose without commit/rollback */ }
            using var tx2 = db.BeginTransaction();
            tx2.CreateNode("A");
            tx2.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnforceExclusiveWriter_does_not_block_readonly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = GraphDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new GraphDatabaseOptions { EnforceExclusiveWriter = true });

            using var tx1 = db.BeginTransaction();
            using var ro = db.BeginReadOnlyTransaction();
            tx1.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
