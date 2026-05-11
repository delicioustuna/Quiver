using FluentAssertions;
using GraphDb.Engine.Client;
using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;
using Xunit;

namespace GraphDb.Engine.Tests;

public sealed class GraphDatabaseTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public GraphDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_test_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(_dir);
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

    [GraphRelationship("KNOWS")]
    private partial class KnowsRel : IGraphRelationship<KnowsRel>
    {
        [GraphProperty]
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

    [GraphNode("Person")]
    private partial class PersonNode : IGraphNode<PersonNode>
    {
        [GraphProperty]
        public string Name { get; set; } = "";

        public static string GraphLabel => "Person";
        public static NodeId Insert(IGraphTransaction tx, PersonNode entity)
        {
            var id = tx.CreateNode("Person");
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            return id;
        }
        public static NodeId InsertIndexed(IGraphTransaction tx, PersonNode entity) => Insert(tx, entity);
        public static PersonNode Load(IGraphTransaction tx, NodeId id)
            => new() { Name = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Name").Utf8StringValue) };
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
        var neighbors = g.V(alice).Out<KnowsRel>().ToList();

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
        var neighbors = g.V(bob).In<KnowsRel>().ToList();

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
        var neighbors = g.V<PersonNode>().Out<KnowsRel>().ToList();

        neighbors.Should().Contain(bob);
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
        var rels = g.V(alice).OutE<KnowsRel>().ToList();

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
        var rels = g.V(bob).InE<KnowsRel>().ToList();

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
        var fromAlice = g.V(alice).BothE<KnowsRel>().ToList();
        var fromBob   = g.V(bob).BothE<KnowsRel>().ToList();

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
        var rels = g.V<PersonNode>().OutE<KnowsRel>().ToList();

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
        var names = g.V<PersonNode>().Values(p => p.Name).ToList();

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

    // ===== Match DSL 型付き overload =====

    [Fact]
    public void Match_typed_Out_and_Load_returns_entity()
    {
        using var tx = _db.BeginTransaction();
        var alice = PersonNode.Insert(tx, new PersonNode { Name = "Alice" });
        var bob   = PersonNode.Insert(tx, new PersonNode { Name = "Bob" });
        tx.CreateRelationship(alice, bob, "KNOWS");

        var g = tx.G(_db.Schema);
        var p = GraphDb.Engine.Client.Match.GraphPattern.Node("p", "Person");
        var q = GraphDb.Engine.Client.Match.GraphPattern.Node("q", "Person");

        var results = g.Match(p.Out<KnowsRel>(q))
                       .Return(ctx => ctx.Load<PersonNode>("q"))
                       .ToList();

        results.Should().ContainSingle().Which.Name.Should().Be("Bob");
        tx.Rollback();
    }
}
