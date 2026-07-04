using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <see cref="IGraphTransaction"/> のハイパーエッジ CRUD 操作を検証する。
/// 入力検証、削除カスケード、スナップショット分離、ロール / 型フィルタを網羅する。
/// </summary>
public sealed class HyperedgeCrudTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public HyperedgeCrudTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hyp2a_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── 基本 CRUD ──────────────────────────────────────────

    [Fact]
    public void CreateHyperedge_and_GetMembers_roundtrips()
    {
        using var db = GraphDatabase.Open(_path);
        HyperedgeId heId;
        NodeId a, b, c;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("Person");
            b = tx.CreateNode("Person");
            c = tx.CreateNode("Place");
            heId = tx.CreateHyperedge("Fact", [
                new("Subject", a),
                new("Object", b),
                new("Location", c),
            ]);
            tx.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            var members = Collect(tx.GetMembers(heId));
            members.Should().HaveCount(3);
            members.Should().Contain(new HyperedgeMember("Subject", a));
            members.Should().Contain(new HyperedgeMember("Object", b));
            members.Should().Contain(new HyperedgeMember("Location", c));
        }
    }

    [Fact]
    public void CreateHyperedge_with_typeId_works()
    {
        using var db = GraphDatabase.Open(_path);
        var typeId = db.Schema.GetOrCreateHyperedgeType("Fact");
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var heId = tx.CreateHyperedge(typeId, [new("S", a), new("O", b)]);
        heId.IsValid.Should().BeTrue();

        var typeName = tx.GetHyperedgeTypeName(typeId);
        typeName.Should().Be("Fact");
    }

    [Fact]
    public void DeleteHyperedge_makes_it_invisible()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var heId = tx.CreateHyperedge("T", [new("R1", a), new("R2", b)]);

        Collect(tx.GetMembers(heId)).Should().HaveCount(2);

        tx.DeleteHyperedge(heId);

        Collect(tx.GetMembers(heId)).Should().BeEmpty();
    }

    [Fact]
    public void DeleteHyperedge_nonexistent_is_noop()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        tx.DeleteHyperedge(new HyperedgeId(99999));
    }

    // ── 入力検証 ──────────────────────────────────────────

    [Fact]
    public void CreateHyperedge_rejects_fewer_than_2_members()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");

        var act = () => tx.CreateHyperedge("T", [new("R", a)]);
        act.Should().Throw<ArgumentException>().WithMessage("*at least 2*");
    }

    [Fact]
    public void CreateHyperedge_rejects_empty_type()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");

        var act = () => tx.CreateHyperedge("", [new("R1", a), new("R2", b)]);
        act.Should().Throw<ArgumentException>().WithMessage("*type*");
    }

    [Fact]
    public void CreateHyperedge_rejects_empty_role()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");

        var act = () => tx.CreateHyperedge("T", [new("", a), new("R2", b)]);
        act.Should().Throw<ArgumentException>().WithMessage("*role*");
    }

    [Fact]
    public void CreateHyperedge_rejects_duplicate_role_node_pair()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");

        var act = () => tx.CreateHyperedge("T", [new("R", a), new("R", a)]);
        act.Should().Throw<ArgumentException>().WithMessage("*Duplicate*");
    }

    [Fact]
    public void CreateHyperedge_rejects_nonexistent_node()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");

        var act = () => tx.CreateHyperedge("T", [new("R1", a), new("R2", new NodeId(99999))]);
        act.Should().Throw<ArgumentException>().WithMessage("*does not exist*");
    }

    [Fact]
    public void CreateHyperedge_allows_same_node_different_roles()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");

        var heId = tx.CreateHyperedge("T", [new("Subject", a), new("Object", a), new("Witness", b)]);
        Collect(tx.GetMembers(heId)).Should().HaveCount(3);
    }

    // ── GetMembers フィルタ ─────────────────────────────────

    [Fact]
    public void GetMembers_filters_by_role()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var c = tx.CreateNode("C");
        var heId = tx.CreateHyperedge("T", [new("Subject", a), new("Object", b), new("Location", c)]);

        var subjects = Collect(tx.GetMembers(heId, role: "Subject"));
        subjects.Should().ContainSingle().Which.NodeId.Should().Be(a);
    }

    [Fact]
    public void GetMembers_unknown_role_returns_empty()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var heId = tx.CreateHyperedge("T", [new("R1", a), new("R2", b)]);

        Collect(tx.GetMembers(heId, role: "NoSuchRole")).Should().BeEmpty();
    }

    // ── GetHyperedges フィルタ ──────────────────────────────

    [Fact]
    public void GetHyperedges_returns_all_for_node()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var c = tx.CreateNode("C");
        var he1 = tx.CreateHyperedge("T1", [new("R1", a), new("R2", b)]);
        var he2 = tx.CreateHyperedge("T2", [new("R1", a), new("R2", c)]);

        var heIds = CollectIds(tx.GetHyperedges(a));
        heIds.Should().HaveCount(2);
        heIds.Should().Contain(he1.Sequence);
        heIds.Should().Contain(he2.Sequence);
    }

    [Fact]
    public void GetHyperedges_filters_by_type()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var c = tx.CreateNode("C");
        tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
        var he2 = tx.CreateHyperedge("Event", [new("S", a), new("O", c)]);

        var heIds = CollectIds(tx.GetHyperedges(a, type: "Event"));
        heIds.Should().ContainSingle().Which.Should().Be(he2.Sequence);
    }

    [Fact]
    public void GetHyperedges_filters_by_role()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var c = tx.CreateNode("C");
        tx.CreateHyperedge("T", [new("Subject", a), new("Object", b)]);
        var he2 = tx.CreateHyperedge("T", [new("Witness", a), new("Object", c)]);

        var heIds = CollectIds(tx.GetHyperedges(a, role: "Witness"));
        heIds.Should().ContainSingle().Which.Should().Be(he2.Sequence);
    }

    [Fact]
    public void GetHyperedges_filters_by_type_and_role()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var c = tx.CreateNode("C");
        var d = tx.CreateNode("D");
        tx.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
        tx.CreateHyperedge("Event", [new("Subject", a), new("Object", c)]);
        var he3 = tx.CreateHyperedge("Event", [new("Witness", a), new("Actor", d)]);

        var heIds = CollectIds(tx.GetHyperedges(a, type: "Event", role: "Witness"));
        heIds.Should().ContainSingle().Which.Should().Be(he3.Sequence);
    }

    [Fact]
    public void GetHyperedges_unknown_type_returns_empty()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        tx.CreateHyperedge("T", [new("R1", a), new("R2", b)]);

        CollectIds(tx.GetHyperedges(a, type: "NoSuchType")).Should().BeEmpty();
    }

    [Fact]
    public void GetHyperedges_deduplicates_when_node_has_multiple_roles()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var heId = tx.CreateHyperedge("T", [new("Subject", a), new("Object", a), new("Witness", b)]);

        var heIds = CollectIds(tx.GetHyperedges(a));
        heIds.Should().ContainSingle().Which.Should().Be(heId.Sequence);
    }

    // ── DeleteNode カスケード ──────────────────────────────

    [Fact]
    public void DeleteNode_cascades_to_hyperedges()
    {
        using var db = GraphDatabase.Open(_path);
        HyperedgeId he1, he2;
        NodeId a, b, c;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("A");
            b = tx.CreateNode("B");
            c = tx.CreateNode("C");
            he1 = tx.CreateHyperedge("T", [new("R1", a), new("R2", b)]);
            he2 = tx.CreateHyperedge("T", [new("R1", a), new("R2", c)]);
            tx.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(a);
            tx.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            Collect(tx.GetMembers(he1)).Should().BeEmpty();
            Collect(tx.GetMembers(he2)).Should().BeEmpty();
            CollectIds(tx.GetHyperedges(b)).Should().BeEmpty();
            CollectIds(tx.GetHyperedges(c)).Should().BeEmpty();
        }
    }

    [Fact]
    public void DeleteNode_cascades_each_hyperedge_once()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        tx.CreateHyperedge("T", [new("Subject", a), new("Object", a), new("Witness", b)]);

        tx.DeleteNode(a);
        CollectIds(tx.GetHyperedges(b)).Should().BeEmpty();
    }

    // ── スナップショット分離 ─────────────────────────────────

    [Fact]
    public void Committed_hyperedge_visible_in_new_transaction()
    {
        using var db = GraphDatabase.Open(_path);
        HyperedgeId heId;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            heId = tx.CreateHyperedge("T", [new("R1", a), new("R2", b)]);
            tx.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            Collect(tx.GetMembers(heId)).Should().HaveCount(2);
        }
    }

    [Fact]
    public void Aborted_hyperedge_invisible()
    {
        using var db = GraphDatabase.Open(_path);
        HyperedgeId heId;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            heId = tx.CreateHyperedge("T", [new("R1", a), new("R2", b)]);
            tx.Rollback();
        }

        using (var tx = db.BeginTransaction())
        {
            Collect(tx.GetMembers(heId)).Should().BeEmpty();
        }
    }

    [Fact]
    public void Older_snapshot_still_sees_deleted_hyperedge()
    {
        using var db = GraphDatabase.Open(_path);
        NodeId a, b;
        HyperedgeId heId;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("A");
            b = tx.CreateNode("B");
            heId = tx.CreateHyperedge("T", [new("R1", a), new("R2", b)]);
            tx.Commit();
        }

        using var reader = db.BeginReadOnlyTransaction();
        var membersBefore = Collect(reader.GetMembers(heId));
        membersBefore.Should().HaveCount(2);

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteHyperedge(heId);
            tx.Commit();
        }

        var membersStillVisible = Collect(reader.GetMembers(heId));
        membersStillVisible.Should().HaveCount(2);
    }

    // ── Schema API ──────────────────────────────────────

    [Fact]
    public void Schema_lists_hyperedge_types_and_roles()
    {
        using var db = GraphDatabase.Open(_path);
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            tx.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
            tx.CreateHyperedge("Event", [new("Actor", a), new("Target", b)]);
            tx.Commit();
        }

        db.Schema.ListHyperedgeTypes().Should().BeEquivalentTo(["Fact", "Event"]);
        db.Schema.ListRoles().Should().BeEquivalentTo(["Subject", "Object", "Actor", "Target"]);
    }

    [Fact]
    public void Schema_TryGetHyperedgeTypeId_works()
    {
        using var db = GraphDatabase.Open(_path);
        var typeId = db.Schema.GetOrCreateHyperedgeType("MyType");
        db.Schema.TryGetHyperedgeTypeId("MyType", out var found).Should().BeTrue();
        found.Should().Be(typeId);

        db.Schema.TryGetHyperedgeTypeId("Unknown", out _).Should().BeFalse();
    }

    // ── helpers ──────────────────────────────────────────

    private static List<HyperedgeMember> Collect(HyperedgeMemberEnumerator e)
    {
        var list = new List<HyperedgeMember>();
        while (e.MoveNext()) list.Add(e.Current);
        return list;
    }

    private static List<long> CollectIds(HyperedgeIdEnumerator e)
    {
        var list = new List<long>();
        while (e.MoveNext()) list.Add(e.Current.Sequence);
        return list;
    }
}
