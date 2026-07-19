using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class QuiverDatabaseTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public QuiverDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_test_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ===== Vertex CRUD =====

    [Fact]
    public void CreateVertex_and_VertexExists()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("Person");
        tx.VertexExists(id).Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void DeleteVertex_makes_it_disappear()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("Person");
        tx.DeleteVertex(id);
        tx.VertexExists(id).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void DeleteVertex_with_edges_succeeds()
    {
        using var tx = _db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        tx.CreateEdge(a, b, "KNOWS");
        tx.DeleteVertex(a);
        tx.VertexExists(a).Should().BeFalse();
        tx.Commit();
    }

    // ===== Edge CRUD =====

    [Fact]
    public void CreateEdge_and_enumerate()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");

        var neighbors = new List<VertexId>();
        var en = tx.EnumerateEdges(alice);
        while (en.MoveNext())
        {
            var edge = en.Current;
            neighbors.Add(edge.Source == alice ? edge.Target : edge.Source);
        }

        neighbors.Should().ContainSingle().Which.Should().Be(bob);
        tx.Commit();
    }

    [Fact]
    public void DeleteEdge_removes_it_from_enumeration()
    {
        using var tx = _db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "LINK");
        tx.DeleteEdge(edge);

        var en = tx.EnumerateEdges(a);
        bool any = en.MoveNext();
        any.Should().BeFalse();
        tx.Commit();
    }

    // ===== Properties =====

    [Fact]
    public void SetProperty_Int64_and_GetProperty()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("Item");
        tx.SetProperty(id, "score", PropertyValue.FromInt64(42L));
        var val = tx.GetProperty(id, "score");
        val.Type.Should().Be(PropertyValueType.Int64);
        val.Int64Value.Should().Be(42L);
        tx.Commit();
    }

    [Fact]
    public void SetProperty_String_and_GetProperty()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString("Alice"));
        var val = tx.GetProperty(id, "name");
        val.Type.Should().Be(PropertyValueType.String);
        System.Text.Encoding.UTF8.GetString(val.Utf8StringValue).Should().Be("Alice");
        tx.Commit();
    }

    [Fact]
    public void HasProperty_returns_correct_values()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("X");
        tx.SetProperty(id, "exists", PropertyValue.FromBool(true));
        tx.HasProperty(id, "exists").Should().BeTrue();
        tx.HasProperty(id, "missing").Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void RemoveProperty_removes_it()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("X");
        tx.SetProperty(id, "age", PropertyValue.FromInt32(30));
        tx.RemoveProperty(id, "age");
        tx.HasProperty(id, "age").Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void SetProperty_overwrites_previous_value()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("X");
        tx.SetProperty(id, "n", PropertyValue.FromInt64(1L));
        tx.SetProperty(id, "n", PropertyValue.FromInt64(99L));
        tx.GetProperty(id, "n").Int64Value.Should().Be(99L);
        tx.Commit();
    }

    // ===== Operators via Execute =====

    [Fact]
    public void AllVerticesScan_returns_all_vertices()
    {
        using var tx = _db.BeginWriteTransaction();
        var a = tx.CreateVertex("X");
        var b = tx.CreateVertex("X");
        var c = tx.CreateVertex("X");

        var scan = new AllVerticesScanOperator();
        using var result = tx.Execute(scan);
        var ids = result.Rows().Select(r => r.GetVertexId(0)).ToList();
        ids.Should().Contain([a, b, c]);
        tx.Rollback();
    }

    [Fact]
    public void AllVerticesScan_with_label_filter()
    {
        using var tx = _db.BeginWriteTransaction();
        tx.CreateVertex("Person");
        tx.CreateVertex("Person");
        tx.CreateVertex("Car");

        var personLabel = tx.EditSchema.GetOrCreateLabel("Person");
        var scan = new AllVerticesScanOperator(personLabel);
        using var result = tx.Execute(scan);
        result.Rows().Should().HaveCount(2);
        tx.Rollback();
    }

    [Fact]
    public void LimitOperator_via_Execute_limits_rows()
    {
        using var tx = _db.BeginWriteTransaction();
        for (int i = 0; i < 5; i++) tx.CreateVertex("N");

        var scan = new AllVerticesScanOperator();
        var limit = new LimitOperator(scan, limit: 3);
        using var result = tx.Execute(limit);
        result.Rows().Should().HaveCount(3);
        tx.Rollback();
    }

    [Fact]
    public void PropertyLookup_via_Execute_returns_string_value()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString("Bob"));

        var nameKey = tx.EditSchema.GetOrCreatePropertyKey("name");
        var scan = new AllVerticesScanOperator();
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
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");

        var source = new VertexByLabelScanOperator(tx.EditSchema.GetOrCreateLabel("Person"));
        var expand = new ExpandOperator(source, sourceVertexColumn: 0,
            Direction.Outgoing, typeFilter: null, ExpandOutputMode.NeighborOnly);
        using var result = tx.Execute(expand);

        var neighbors = result.Rows().Select(r => r.GetVertexId(0)).ToList();
        neighbors.Should().Contain(bob);
        tx.Rollback();
    }

    // ===== Edge Properties =====

    [Fact]
    public void SetProperty_on_edge_and_GetProperty()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var edge   = tx.CreateEdge(alice, bob, "KNOWS");

        tx.SetProperty(edge, "since", PropertyValue.FromInt64(2020L));
        var val = tx.GetProperty(edge, "since");

        val.Type.Should().Be(PropertyValueType.Int64);
        val.Int64Value.Should().Be(2020L);
        tx.Commit();
    }

    [Fact]
    public void SetProperty_on_edge_overwrites_previous_value()
    {
        using var tx = _db.BeginWriteTransaction();
        var a   = tx.CreateVertex("A");
        var b   = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "LINK");

        tx.SetProperty(edge, "weight", PropertyValue.FromDouble(1.0));
        tx.SetProperty(edge, "weight", PropertyValue.FromDouble(9.9));
        var val = tx.GetProperty(edge, "weight");

        val.Type.Should().Be(PropertyValueType.Double);
        val.DoubleValue.Should().BeApproximately(9.9, 1e-9);
        tx.Commit();
    }

    [Fact]
    public void GetProperty_on_edge_returns_default_for_missing_key()
    {
        using var tx = _db.BeginWriteTransaction();
        var a   = tx.CreateVertex("A");
        var b   = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "LINK");

        var val = tx.GetProperty(edge, "nonexistent");
        val.Type.Should().Be(default(PropertyValueType));
        tx.Rollback();
    }

    [Fact]
    public void DeleteEdge_frees_its_properties()
    {
        using var tx = _db.BeginWriteTransaction();
        var a   = tx.CreateVertex("A");
        var b   = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "LINK");
        tx.SetProperty(edge, "since", PropertyValue.FromInt64(2021L));

        tx.DeleteEdge(edge);

        var en = tx.EnumerateEdges(a);
        en.MoveNext().Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void DeleteVertex_with_edge_properties_succeeds()
    {
        using var tx = _db.BeginWriteTransaction();
        var a   = tx.CreateVertex("A");
        var b   = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "LINK");
        tx.SetProperty(edge, "x", PropertyValue.FromInt32(42));

        tx.DeleteVertex(a);
        tx.VertexExists(a).Should().BeFalse();
        tx.Commit();
    }

    // ===== 型付き Traversal API =====

    [Fact]
    public void Edge_generation_lookup_survives_reopen_and_adjacency_compact()
    {
        var path = Path.Combine(_dir, "edge_locator_reopen.quiver");
        EdgeId stamped;

        using (var db = QuiverDatabase.Open(path))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                var a = tx.CreateVertex("A");
                var b = tx.CreateVertex("B");
                var edge = tx.CreateEdge(a, b, "LINK");
                stamped = EdgeId.Create(edge.Sequence, 1);
                tx.SetProperty(edge, "weight", PropertyValue.FromInt64(10));
                tx.Commit();
            }

            db.CompactAdjacency();
        }

        using (var db = QuiverDatabase.Open(path))
        {
            using var tx = db.BeginReadTransaction();
            var value = tx.GetProperty(stamped, "weight");
            value.Type.Should().Be(PropertyValueType.Int64);
            value.Int64Value.Should().Be(10);
        }

        using (var db = QuiverDatabase.Open(path))
        {
            db.CompactAdjacency();
            using var tx = db.BeginReadTransaction();
            var value = tx.GetProperty(stamped, "weight");
            value.Type.Should().Be(PropertyValueType.Int64);
            value.Int64Value.Should().Be(10);
        }
    }

    [Fact]
    public void Vacuum_keeps_raw_edge_sequence_from_retargeting()
    {
        var path = Path.Combine(_dir, "edge_locator_reuse.quiver");
        using var db = QuiverDatabase.Open(path);

        VertexId a;
        VertexId b;
        VertexId c;
        EdgeId old;
        using (var tx = db.BeginWriteTransaction())
        {
            a = tx.CreateVertex("A");
            b = tx.CreateVertex("B");
            c = tx.CreateVertex("C");
            old = tx.CreateEdge(a, b, "LINK");
            tx.SetProperty(old, "weight", PropertyValue.FromInt64(1));
            tx.Commit();
        }

        var stale = old;
        var raw = new EdgeId(old.Sequence);
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteEdge(old);
            tx.Commit();
        }

        db.Vacuum().ReclaimedEdges.Should().Be(1);

        EdgeId replacement;
        using (var tx = db.BeginWriteTransaction())
        {
            replacement = tx.CreateEdge(a, c, "LINK");
            tx.SetProperty(replacement, "weight", PropertyValue.FromInt64(2));
            tx.Commit();
        }

        replacement.Sequence.Should().BeGreaterThan(old.Sequence);
        db.CompactAdjacency();

        using (var tx = db.BeginWriteTransaction())
        {
            tx.GetProperty(stale, "weight").Type.Should().Be(default(PropertyValueType));
            tx.GetProperty(raw, "weight").Type.Should().Be(default(PropertyValueType));
            tx.SetProperty(stale, "weight", PropertyValue.FromInt64(99));
            tx.DeleteEdge(stale);
            tx.Commit();
        }

        using (var tx = db.BeginReadTransaction())
        {
            tx.GetProperty(replacement, "weight").Int64Value.Should().Be(2);

            var targets = new List<VertexId>();
            var edges = tx.EnumerateEdges(a, Direction.Outgoing);
            while (edges.MoveNext())
                targets.Add(edges.Current.Target);

            targets.Should().ContainSingle().Which.Should().Be(c);
        }
    }

    private partial class KnowsEdge : IGraphEdge<KnowsEdge, PersonVertex, PersonVertex>
    {
        [Property]
        public int Since { get; set; }

        public static string GraphType => "KNOWS";
        public static EdgeId Insert(IWriteTransaction tx, VertexId from, VertexId to, KnowsEdge entity)
        {
            var id = tx.CreateEdge(from, to, "KNOWS");
            tx.SetProperty(id, "Since", PropertyValue.FromInt32(entity.Since));
            return id;
        }
        public static KnowsEdge Load(IReadTransaction tx, EdgeId id)
            => new() { Since = tx.GetProperty(id, "Since").Int32Value };
        public static void Update(IWriteTransaction tx, EdgeId id, KnowsEdge entity)
            => tx.SetProperty(id, "Since", PropertyValue.FromInt32(entity.Since));
        public static void Delete(IWriteTransaction tx, EdgeId id)
            => tx.DeleteEdge(id);
    }

    private partial class PersonVertex : IGraphVertex<PersonVertex>
    {
        public string Name { get; set; } = "";
        public double Score { get; set; }
        public DateTime CreatedAt { get; set; }

        public static string GraphLabel => "Person";
        public static VertexId Insert(IWriteTransaction tx, PersonVertex entity)
        {
            var id = tx.CreateVertex("Person");
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Score", PropertyValue.FromDouble(entity.Score));
            tx.SetProperty(id, "CreatedAt", PropertyValue.FromDateTime(entity.CreatedAt));
            return id;
        }
        public static VertexId InsertIndexed(IWriteTransaction tx, PersonVertex entity) => Insert(tx, entity);
        public static PersonVertex Load(IReadTransaction tx, VertexId id)
            => new()
            {
                Name = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Name").Utf8StringValue),
                Score = tx.GetProperty(id, "Score").DoubleValue,
                CreatedAt = tx.GetProperty(id, "CreatedAt").DateTimeValue,
            };
        public static void Update(IWriteTransaction tx, VertexId id, PersonVertex entity)
            => tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
        public static void Delete(IWriteTransaction tx, VertexId id) => tx.DeleteVertex(id);
    }

    [Fact]
    public void TypedTraversal_Out_generic_finds_neighbor()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");

        var g = tx.Query;
        var neighbors = g.Vertex(alice).Out<KnowsEdge>().ToList();

        neighbors.Should().ContainSingle().Which.Should().Be(bob);
        tx.Rollback();
    }

    [Fact]
    public void TypedTraversal_In_generic_finds_neighbor()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");

        var g = tx.Query;
        var neighbors = g.Vertex(bob).In<KnowsEdge>().ToList();

        neighbors.Should().ContainSingle().Which.Should().Be(alice);
        tx.Rollback();
    }

    [Fact]
    public void TypedGraphTraversal_Out_generic_finds_neighbor()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = PersonVertex.Insert(tx, new PersonVertex { Name = "Alice" });
        var bob   = PersonVertex.Insert(tx, new PersonVertex { Name = "Bob" });
        tx.CreateEdge(alice, bob, "KNOWS");

        var g = tx.Query;
        // Out<TEdge, TTarget>() は TypedGraphTraversal<PersonVertex> を型保存する。
        var neighbors = g.Vertices<PersonVertex>().Out<KnowsEdge, PersonVertex>().ToListWithIds();

        neighbors.Select(n => n.Id).Should().Contain(bob);
        tx.Rollback();
    }

    // ===== 式ツリー述語 (Where / OutWhere) =====

    [Fact]
    public void TypedGraphTraversal_Where_expression_filters_vertices()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = PersonVertex.Insert(tx, new PersonVertex { Name = "Alice" });
        var bob   = PersonVertex.Insert(tx, new PersonVertex { Name = "Bob" });

        var g = tx.Query;
        var found = g.Vertices<PersonVertex>()
                     .Where(p => p.Name == "Alice" || p.Name.StartsWith("Al"))
                     .ToListWithIds();

        found.Select(n => n.Id).Should().Contain(alice).And.NotContain(bob);
        tx.Rollback();
    }

    [Fact]
    public void TypedGraphTraversal_OutWhere_edge_predicate_filters()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = PersonVertex.Insert(tx, new PersonVertex { Name = "Alice" });
        var bob   = PersonVertex.Insert(tx, new PersonVertex { Name = "Bob" });
        var carol = PersonVertex.Insert(tx, new PersonVertex { Name = "Carol" });
        KnowsEdge.Insert(tx, alice, bob,   new KnowsEdge { Since = 2020 });
        KnowsEdge.Insert(tx, alice, carol, new KnowsEdge { Since = 2024 });

        var g = tx.Query;
        // Edgeプロパティ Since で絞り込みつつ PersonVertex 型を保存して対象へ進む。
        var recent = g.Vertices<PersonVertex>()
                      .Where(p => p.Name == "Alice")
                      .OutWhere<KnowsEdge, PersonVertex>(e => e.Since > 2022)
                      .ToListWithIds();

        recent.Select(n => n.Id).Should().Contain(carol).And.NotContain(bob);
        tx.Rollback();
    }

    // ===== 浮動小数点の範囲述語 =====

    [Fact]
    public void Has_double_range_filters_via_predicate()
    {
        using var tx = _db.BeginWriteTransaction();
        var a = tx.CreateVertex("Item"); tx.SetProperty(a, "score", PropertyValue.FromDouble(1.5));
        var b = tx.CreateVertex("Item"); tx.SetProperty(b, "score", PropertyValue.FromDouble(2.5));

        var g = tx.Query;
        var hi = g.Vertices().HasLabel("Item").Has("score", P.Gt(2.0)).ToList();

        hi.Should().ContainSingle().Which.Should().Be(b);
        tx.Rollback();
    }

    [Fact]
    public void TypedWhere_double_range_filters()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = PersonVertex.Insert(tx, new PersonVertex { Name = "Alice", Score = 1.5 });
        var bob   = PersonVertex.Insert(tx, new PersonVertex { Name = "Bob",   Score = 2.5 });

        var g = tx.Query;
        // double メンバーの式ツリー比較では、整数リテラルも double 比較へ振り分ける。
        var found = g.Vertices<PersonVertex>().Where(p => p.Score > 2).ToListWithIds();

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
        using var tx = _db.BeginWriteTransaction();
        var utc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var alice = PersonVertex.Insert(tx, new PersonVertex { Name = "Alice", CreatedAt = utc.AddDays(1) });
        var bob   = PersonVertex.Insert(tx, new PersonVertex { Name = "Bob",   CreatedAt = utc.AddDays(10) });

        var g = tx.Query;
        // 同一瞬時を Local で渡しても UTC へ正準化され一貫比較される。
        var cutoffLocal = utc.AddDays(5).ToLocalTime();
        var recent = g.Vertices<PersonVertex>().Where(p => p.CreatedAt > cutoffLocal).ToListWithIds();

        recent.Select(n => n.Id).Should().Contain(bob).And.NotContain(alice);
        tx.Rollback();
    }

    [Fact]
    public void EdgeTraversal_Has_filters_edge_properties()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var carol = tx.CreateVertex("Person");
        var r1 = tx.CreateEdge(alice, bob,   "KNOWS");
        var r2 = tx.CreateEdge(alice, carol, "KNOWS");
        tx.SetProperty(r1, "since", PropertyValue.FromInt64(2020));
        tx.SetProperty(r2, "since", PropertyValue.FromInt64(2024));

        var g = tx.Query;
        // Edgeトラバーサルの .Has はEdgeプロパティを読む。
        var edges = g.Vertex(alice).OutEdges("KNOWS").Has("since", P.Gt(2022L)).ToList();

        edges.Should().ContainSingle().Which.Should().Be(r2);
        tx.Rollback();
    }

    // ===== OutE<TEdge> / InE<TEdge> / BothE<TEdge> =====

    [Fact]
    public void OutE_generic_returns_edge_id()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var edge   = tx.CreateEdge(alice, bob, "KNOWS");

        var g    = tx.Query;
        var edges = g.Vertex(alice).OutEdges<KnowsEdge>().ToList();

        edges.Should().ContainSingle().Which.Should().Be(edge);
        tx.Rollback();
    }

    [Fact]
    public void InE_generic_returns_edge_id()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var edge   = tx.CreateEdge(alice, bob, "KNOWS");

        var g    = tx.Query;
        var edges = g.Vertex(bob).InEdges<KnowsEdge>().ToList();

        edges.Should().ContainSingle().Which.Should().Be(edge);
        tx.Rollback();
    }

    [Fact]
    public void BothE_generic_returns_both_directions()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var edge   = tx.CreateEdge(alice, bob, "KNOWS");

        var g    = tx.Query;
        var fromAlice = g.Vertex(alice).BothEdges<KnowsEdge>().ToList();
        var fromBob   = g.Vertex(bob).BothEdges<KnowsEdge>().ToList();

        fromAlice.Should().ContainSingle().Which.Should().Be(edge);
        fromBob.Should().ContainSingle().Which.Should().Be(edge);
        tx.Rollback();
    }

    [Fact]
    public void TypedTraversal_OutE_generic_returns_edge_id()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = PersonVertex.Insert(tx, new PersonVertex { Name = "Alice" });
        var bob   = PersonVertex.Insert(tx, new PersonVertex { Name = "Bob" });
        var edge   = KnowsEdge.Insert(tx, alice, bob, new KnowsEdge { Since = 2020 });

        var g    = tx.Query;
        var edges = g.Vertices<PersonVertex>().OutEdges<KnowsEdge>().ToList();

        edges.Should().Contain(edge);
        tx.Rollback();
    }

    // ===== Values<TProp>(expr) =====

    [Fact]
    public void TypedTraversal_Values_expression_returns_property()
    {
        using var tx = _db.BeginWriteTransaction();
        PersonVertex.Insert(tx, new PersonVertex { Name = "Alice" });
        PersonVertex.Insert(tx, new PersonVertex { Name = "Bob" });

        var g     = tx.Query;
        var names = g.Vertices<PersonVertex>().Values(p => p.Name).ToList();

        names.Should().Contain("Alice").And.Contain("Bob");
        tx.Rollback();
    }

    // ===== SeekIndex / RangeIndex =====

    [Fact]
    public void SeekIndex_string_equality_finds_vertex()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
        tx.SetProperty(bob,   "name", PropertyValue.FromString("Bob"));
        tx.SetIndexedProperty("idx_name", "Alice", alice);
        tx.SetIndexedProperty("idx_name", "Bob",   bob);

        var key = PropertyValue.FromString("Alice");
        var en = tx.SeekIndex("idx_name", key);
        var results = new List<VertexId>();
        while (en.MoveNext())
            if (en.Current.Kind == EntityKind.Vertex)
                results.Add(new VertexId(en.Current.Value));
        en.Dispose();

        results.Should().ContainSingle().Which.Should().Be(alice);
        tx.Rollback();
    }

    [Fact]
    public void SeekIndex_int64_equality_finds_vertex()
    {
        using var tx = _db.BeginWriteTransaction();
        var n42 = tx.CreateVertex("Item");
        var n99 = tx.CreateVertex("Item");
        tx.SetIndexedProperty("idx_score", 42L, n42);
        tx.SetIndexedProperty("idx_score", 99L, n99);

        var key = PropertyValue.FromInt64(42L);
        var en = tx.SeekIndex("idx_score", key);
        var results = new List<VertexId>();
        while (en.MoveNext())
            if (en.Current.Kind == EntityKind.Vertex)
                results.Add(new VertexId(en.Current.Value));
        en.Dispose();

        results.Should().ContainSingle().Which.Should().Be(n42);
        tx.Rollback();
    }

    [Fact]
    public void RangeIndex_int64_returns_vertices_in_range()
    {
        using var tx = _db.BeginWriteTransaction();
        var n10 = tx.CreateVertex("Item");
        var n20 = tx.CreateVertex("Item");
        var n30 = tx.CreateVertex("Item");
        tx.SetIndexedProperty("idx_val", 10L, n10);
        tx.SetIndexedProperty("idx_val", 20L, n20);
        tx.SetIndexedProperty("idx_val", 30L, n30);

        var from = PropertyValue.FromInt64(10L);
        var to   = PropertyValue.FromInt64(25L);
        var en = tx.RangeIndex("idx_val", from, fromInclusive: true, to, toInclusive: true);
        var results = new List<VertexId>();
        while (en.MoveNext())
            if (en.Current.Kind == EntityKind.Vertex)
                results.Add(new VertexId(en.Current.Value));
        en.Dispose();

        results.Should().Contain(n10).And.Contain(n20).And.NotContain(n30);
        tx.Rollback();
    }

    [Fact]
    public void SeekIndex_on_nonexistent_index_returns_empty()
    {
        using var tx = _db.BeginWriteTransaction();
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
                using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
            }

            // 単一ファイルコンテナでは、すべてのコアデータを graph.quiver に格納する。
            byte[] dataSnap = File.ReadAllBytes(Path.Combine(dir, "graph.quiver"));

            VertexId aliceId;

            // データを書き込んでコミットする。WAL はフラッシュされるが、バッファープールは未反映でもよい。
            // 正常終了時は Dispose が全ページのフラッシュと WAL の削除を行うため、
            // graph.quiver が確定して WAL が消える。WAL replay 経路を検証するため、未コミットの tx を
            // 1 つ開いたまま Dispose して「クラッシュ (ActiveCount>0 → WAL 非削除)」を模擬する。
            {
                var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
                using (var tx = db.BeginWriteTransaction())
                {
                    aliceId = tx.CreateVertex("Person");
                    tx.SetProperty(aliceId, "name", PropertyValue.FromString("Alice"));
                    tx.Commit();
                }
                _ = db.BeginWriteTransaction(); // 未コミットのまま放置 → ActiveCount>0 → クリーン終了抑止 → WAL 残存
                db.Dispose();
            }

            // Restore pre-write data file to simulate crash (buffer not written to disk).
            File.WriteAllBytes(Path.Combine(dir, "graph.quiver"), dataSnap);

            // 再オープンし、RecoveryManager が WAL の PageImage レコードを再実行する。
            {
                using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
                using var tx = db.BeginReadTransaction();
                tx.VertexExists(aliceId).Should().BeTrue("WAL recovery must restore the committed vertex");
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
        using var tx = _db.BeginWriteTransaction();
        var alice = PersonVertex.Insert(tx, new PersonVertex { Name = "Alice" });
        var bob   = PersonVertex.Insert(tx, new PersonVertex { Name = "Bob" });
        tx.CreateEdge(alice, bob, "KNOWS");

        var g = tx.Query;
        var p = Quiver.Api.Match.GraphPattern.Vertex("p", "Person");
        var q = Quiver.Api.Match.GraphPattern.Vertex("q", "Person");

        var results = g.Match(p.Out<KnowsEdge>(q))
                       .Return(ctx => ctx.Load<PersonVertex>("q"))
                       .ToList();

        results.Should().ContainSingle().Which.Name.Should().Be("Bob");
        tx.Rollback();
    }

    // ===== ストリーミングカーソル =====

    [Fact]
    public void GraphTraversal_AsEnumerable_streams_without_full_materialise()
    {
        using var tx = _db.BeginWriteTransaction();
        tx.CreateVertex("Person");
        tx.CreateVertex("Person");
        tx.CreateVertex("Person");
        tx.Commit();

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        int count = 0;
        foreach (var _ in g.Vertices().HasLabel("Person").AsEnumerable())
            count++;

        count.Should().Be(3);
    }

    [Fact]
    public void GraphTraversal_AsCursor_streams_results()
    {
        using var tx = _db.BeginWriteTransaction();
        for (int i = 0; i < 5; i++)
        {
            var id = tx.CreateVertex("Counter");
            tx.SetProperty(id, "n", PropertyValue.FromInt64(i));
        }
        tx.Commit();

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var names = new List<string>();
        using (var cursor = g.Vertices().HasLabel("Counter").Values("n").AsCursor())
        {
            while (cursor.MoveNext())
                names.Add(cursor.Current);
        }

        names.Should().HaveCount(5);
    }

    [Fact]
    public void MatchQuery_AsCursor_streams_results()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("StreamPerson");
        tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
        var bob = tx.CreateVertex("StreamPerson");
        tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));
        tx.CreateEdge(alice, bob, "STREAM_KNOWS");
        tx.Commit();

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;
        var p = Quiver.Api.Match.GraphPattern.Vertex("p", "StreamPerson");
        var q = Quiver.Api.Match.GraphPattern.Vertex("q", "StreamPerson");
        var found = new List<string>();
        using (var cursor = g.Match(p.Out("STREAM_KNOWS", q)).Return(ctx => ctx["q"].Get<string>("name")).AsCursor())
        {
            while (cursor.MoveNext())
                found.Add(cursor.Current);
        }

        found.Should().ContainSingle().Which.Should().Be("Bob");
    }

    // ===== サブトラバーサル述語 =====

    [Fact]
    public void Where_out_exists_keeps_only_vertices_with_neighbor()
    {
        // alice → bob (KNOWS), charlie has no edges
        using var tx = _db.BeginWriteTransaction();
        var alice   = tx.CreateVertex("Person");
        var bob     = tx.CreateVertex("Person");
        var charlie = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");
        tx.Commit();

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var results = g.Vertices().HasLabel("Person")
                          .Where(t => t.Out("KNOWS"))
                          .ToList();

        results.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void Not_out_exists_keeps_only_vertices_without_neighbor()
    {
        using var tx = _db.BeginWriteTransaction();
        var alice   = tx.CreateVertex("Person");
        var bob     = tx.CreateVertex("Person");
        var charlie = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob, "KNOWS");
        tx.Commit();

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var results = g.Vertices().HasLabel("Person")
                          .Not(t => t.Out("KNOWS"))
                          .ToList();

        results.Should().HaveCount(2);
        results.Should().Contain(bob).And.Contain(charlie);
        results.Should().NotContain(alice);
    }

    [Fact]
    public void Where_out_with_property_filter_passes_only_matching_neighbors()
    {
        // alice → bob("name"="Bob"), dave → eve("name"="Eve")
        // Where(t => t.Out("KNOWS").Has("name", "Bob")) should return only alice
        using var tx = _db.BeginWriteTransaction();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var dave  = tx.CreateVertex("Person");
        var eve   = tx.CreateVertex("Person");
        tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));
        tx.SetProperty(eve, "name", PropertyValue.FromString("Eve"));
        tx.CreateEdge(alice, bob, "KNOWS");
        tx.CreateEdge(dave, eve, "KNOWS");
        tx.Commit();

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var results = g.Vertices().HasLabel("Person")
                          .Where(t => t.Out("KNOWS").Has("name", "Bob"))
                          .ToList();

        results.Should().ContainSingle().Which.Should().Be(alice);
    }

    [Fact]
    public void Where_multiple_vertices_each_probed_independently()
    {
        // 10 vertices, even-indexed ones each have a KNOWS edge
        using var tx = _db.BeginWriteTransaction();
        var vertices = new VertexId[10];
        for (int i = 0; i < 10; i++)
            vertices[i] = tx.CreateVertex("Item");
        for (int i = 0; i < 10; i += 2)
        {
            var target = tx.CreateVertex("Target");
            tx.CreateEdge(vertices[i], target, "LINKS");
        }
        tx.Commit();

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var results = g.Vertices().HasLabel("Item")
                          .Where(t => t.Out("LINKS"))
                          .ToList();

        results.Should().HaveCount(5);
    }

    // ===== writer contention =====

    [Fact]
    public void Fail_fast_contention_blocks_second_writer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = QuiverDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new QuiverDatabaseOptions { WriterContentionMode = WriterContentionMode.FailFast });

            using var tx1 = db.BeginWriteTransaction();
            var act = () => db.BeginWriteTransaction();
            act.Should().Throw<WriterBusyException>();
            tx1.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Writer_lease_is_available_after_commit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = QuiverDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new QuiverDatabaseOptions { WriterContentionMode = WriterContentionMode.FailFast });

            using (var tx1 = db.BeginWriteTransaction()) { tx1.Commit(); }
            using var tx2 = db.BeginWriteTransaction();
            tx2.CreateVertex("A");
            tx2.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Writer_lease_is_available_after_rollback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = QuiverDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new QuiverDatabaseOptions { WriterContentionMode = WriterContentionMode.FailFast });

            using (var tx1 = db.BeginWriteTransaction()) { tx1.Rollback(); }
            using var tx2 = db.BeginWriteTransaction();
            tx2.CreateVertex("A");
            tx2.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Writer_lease_is_available_after_dispose_without_commit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = QuiverDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new QuiverDatabaseOptions { WriterContentionMode = WriterContentionMode.FailFast });

            using (db.BeginWriteTransaction()) { /* dispose without commit/rollback */ }
            using var tx2 = db.BeginWriteTransaction();
            tx2.CreateVertex("A");
            tx2.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Active_writer_does_not_block_readonly_transaction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_excl_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = QuiverDatabase.Open(
                Path.Combine(dir, "g.quiver"),
                new QuiverDatabaseOptions { WriterContentionMode = WriterContentionMode.FailFast });

            using var tx1 = db.BeginWriteTransaction();
            using var ro = db.BeginReadTransaction();
            tx1.Commit();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
