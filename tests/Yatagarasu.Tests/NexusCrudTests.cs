using FluentAssertions;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// <see cref="IWriteTransaction"/> のNexus CRUD 操作を検証する。
/// 入力検証、削除カスケード、スナップショット分離、ロール / 型フィルタを網羅する。
/// </summary>
public sealed class NexusCrudTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public NexusCrudTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_hyp2a_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.yata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── 基本 CRUD ──────────────────────────────────────────

    [Fact]
    public void CreateNexus_and_GetMembers_roundtrips()
    {
        using var db = YatagarasuDatabase.Open(_path);
        NexusId heId;
        VertexId a, b, c;
        using (var tx = db.BeginWriteTransaction())
        {
            a = tx.CreateVertex("Person");
            b = tx.CreateVertex("Person");
            c = tx.CreateVertex("Place");
            heId = tx.CreateNexus("Fact", [
                new("Subject", a),
                new("Object", b),
                new("Location", c),
            ]);
            tx.Commit();
        }

        using (var tx = db.BeginWriteTransaction())
        {
            var members = Collect(tx.GetMembers(heId));
            members.Should().HaveCount(3);
            members.Should().Contain(new NexusMember("Subject", a));
            members.Should().Contain(new NexusMember("Object", b));
            members.Should().Contain(new NexusMember("Location", c));
        }
    }

    [Fact]
    public void CreateNexus_with_typeId_works()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var typeId = db.EditSchema(schema => schema.GetOrCreateNexusType("Fact"));
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var heId = tx.CreateNexus(typeId, [new("S", a), new("O", b)]);
        heId.IsValid.Should().BeTrue();

        var typeName = tx.GetNexusTypeName(typeId);
        typeName.Should().Be("Fact");
    }

    [Fact]
    public void DeleteNexus_makes_it_invisible()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var heId = tx.CreateNexus("T", [new("R1", a), new("R2", b)]);

        Collect(tx.GetMembers(heId)).Should().HaveCount(2);

        tx.DeleteNexus(heId);

        Collect(tx.GetMembers(heId)).Should().BeEmpty();
    }

    [Fact]
    public void DeleteNexus_nonexistent_is_noop()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        tx.DeleteNexus(new NexusId(99999));
    }

    // ── 入力検証 ──────────────────────────────────────────

    [Fact]
    public void CreateNexus_rejects_fewer_than_2_members()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");

        var act = () => tx.CreateNexus("T", [new("R", a)]);
        act.Should().Throw<ArgumentException>().WithMessage("*at least 2*");
    }

    [Fact]
    public void CreateNexus_rejects_empty_type()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");

        var act = () => tx.CreateNexus("", [new("R1", a), new("R2", b)]);
        act.Should().Throw<ArgumentException>().WithMessage("*type*");
    }

    [Fact]
    public void CreateNexus_rejects_empty_role()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");

        var act = () => tx.CreateNexus("T", [new("", a), new("R2", b)]);
        act.Should().Throw<ArgumentException>().WithMessage("*role*");
    }

    [Fact]
    public void CreateNexus_rejects_duplicate_role_vertex_pair()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");

        var act = () => tx.CreateNexus("T", [new("R", a), new("R", a)]);
        act.Should().Throw<ArgumentException>().WithMessage("*Duplicate*");
    }

    [Fact]
    public void CreateNexus_rejects_nonexistent_vertex()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");

        var act = () => tx.CreateNexus("T", [new("R1", a), new("R2", new VertexId(99999))]);
        act.Should().Throw<ArgumentException>().WithMessage("*does not exist*");
    }

    [Fact]
    public void CreateNexus_allows_same_vertex_different_roles()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");

        var heId = tx.CreateNexus("T", [new("Subject", a), new("Object", a), new("Witness", b)]);
        Collect(tx.GetMembers(heId)).Should().HaveCount(3);
    }

    // ── GetMembers フィルタ ─────────────────────────────────

    [Fact]
    public void GetMembers_filters_by_role()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var c = tx.CreateVertex("C");
        var heId = tx.CreateNexus("T", [new("Subject", a), new("Object", b), new("Location", c)]);

        var subjects = Collect(tx.GetMembers(heId, role: "Subject"));
        subjects.Should().ContainSingle().Which.VertexId.Should().Be(a);
    }

    [Fact]
    public void GetMembers_unknown_role_returns_empty()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var heId = tx.CreateNexus("T", [new("R1", a), new("R2", b)]);

        Collect(tx.GetMembers(heId, role: "NoSuchRole")).Should().BeEmpty();
    }

    // ── GetNexuses フィルタ ──────────────────────────────

    [Fact]
    public void GetNexuses_returns_all_for_vertex()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var c = tx.CreateVertex("C");
        var he1 = tx.CreateNexus("T1", [new("R1", a), new("R2", b)]);
        var he2 = tx.CreateNexus("T2", [new("R1", a), new("R2", c)]);

        var heIds = CollectIds(tx.GetNexuses(a));
        heIds.Should().HaveCount(2);
        heIds.Should().Contain(he1.Sequence);
        heIds.Should().Contain(he2.Sequence);
    }

    [Fact]
    public void GetNexuses_filters_by_type()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var c = tx.CreateVertex("C");
        tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
        var he2 = tx.CreateNexus("Event", [new("S", a), new("O", c)]);

        var heIds = CollectIds(tx.GetNexuses(a, type: "Event"));
        heIds.Should().ContainSingle().Which.Should().Be(he2.Sequence);
    }

    [Fact]
    public void GetNexuses_filters_by_role()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var c = tx.CreateVertex("C");
        tx.CreateNexus("T", [new("Subject", a), new("Object", b)]);
        var he2 = tx.CreateNexus("T", [new("Witness", a), new("Object", c)]);

        var heIds = CollectIds(tx.GetNexuses(a, role: "Witness"));
        heIds.Should().ContainSingle().Which.Should().Be(he2.Sequence);
    }

    [Fact]
    public void GetNexuses_filters_by_type_and_role()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var c = tx.CreateVertex("C");
        var d = tx.CreateVertex("D");
        tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
        tx.CreateNexus("Event", [new("Subject", a), new("Object", c)]);
        var he3 = tx.CreateNexus("Event", [new("Witness", a), new("Actor", d)]);

        var heIds = CollectIds(tx.GetNexuses(a, type: "Event", role: "Witness"));
        heIds.Should().ContainSingle().Which.Should().Be(he3.Sequence);
    }

    [Fact]
    public void GetNexuses_unknown_type_returns_empty()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        tx.CreateNexus("T", [new("R1", a), new("R2", b)]);

        CollectIds(tx.GetNexuses(a, type: "NoSuchType")).Should().BeEmpty();
    }

    [Fact]
    public void GetNexuses_deduplicates_when_vertex_has_multiple_roles()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var heId = tx.CreateNexus("T", [new("Subject", a), new("Object", a), new("Witness", b)]);

        var heIds = CollectIds(tx.GetNexuses(a));
        heIds.Should().ContainSingle().Which.Should().Be(heId.Sequence);
    }

    // ── DeleteVertex カスケード ──────────────────────────────

    [Fact]
    public void DeleteVertex_cascades_to_nexuses()
    {
        using var db = YatagarasuDatabase.Open(_path);
        NexusId he1, he2;
        VertexId a, b, c;
        using (var tx = db.BeginWriteTransaction())
        {
            a = tx.CreateVertex("A");
            b = tx.CreateVertex("B");
            c = tx.CreateVertex("C");
            he1 = tx.CreateNexus("T", [new("R1", a), new("R2", b)]);
            he2 = tx.CreateNexus("T", [new("R1", a), new("R2", c)]);
            tx.Commit();
        }

        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(a);
            tx.Commit();
        }

        using (var tx = db.BeginWriteTransaction())
        {
            Collect(tx.GetMembers(he1)).Should().BeEmpty();
            Collect(tx.GetMembers(he2)).Should().BeEmpty();
            CollectIds(tx.GetNexuses(b)).Should().BeEmpty();
            CollectIds(tx.GetNexuses(c)).Should().BeEmpty();
        }
    }

    [Fact]
    public void DeleteVertex_cascades_each_nexus_once()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        tx.CreateNexus("T", [new("Subject", a), new("Object", a), new("Witness", b)]);

        tx.DeleteVertex(a);
        CollectIds(tx.GetNexuses(b)).Should().BeEmpty();
    }

    // ── スナップショット分離 ─────────────────────────────────

    [Fact]
    public void Committed_nexus_visible_in_new_transaction()
    {
        using var db = YatagarasuDatabase.Open(_path);
        NexusId heId;
        using (var tx = db.BeginWriteTransaction())
        {
            var a = tx.CreateVertex("A");
            var b = tx.CreateVertex("B");
            heId = tx.CreateNexus("T", [new("R1", a), new("R2", b)]);
            tx.Commit();
        }

        using (var tx = db.BeginWriteTransaction())
        {
            Collect(tx.GetMembers(heId)).Should().HaveCount(2);
        }
    }

    [Fact]
    public void Aborted_nexus_invisible()
    {
        using var db = YatagarasuDatabase.Open(_path);
        NexusId heId;
        using (var tx = db.BeginWriteTransaction())
        {
            var a = tx.CreateVertex("A");
            var b = tx.CreateVertex("B");
            heId = tx.CreateNexus("T", [new("R1", a), new("R2", b)]);
            tx.Rollback();
        }

        using (var tx = db.BeginWriteTransaction())
        {
            Collect(tx.GetMembers(heId)).Should().BeEmpty();
        }
    }

    [Fact]
    public void Older_snapshot_still_sees_deleted_nexus()
    {
        using var db = YatagarasuDatabase.Open(_path);
        VertexId a, b;
        NexusId heId;
        using (var tx = db.BeginWriteTransaction())
        {
            a = tx.CreateVertex("A");
            b = tx.CreateVertex("B");
            heId = tx.CreateNexus("T", [new("R1", a), new("R2", b)]);
            tx.Commit();
        }

        using var reader = db.BeginReadTransaction();
        var membersBefore = Collect(reader.GetMembers(heId));
        membersBefore.Should().HaveCount(2);

        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteNexus(heId);
            tx.Commit();
        }

        var membersStillVisible = Collect(reader.GetMembers(heId));
        membersStillVisible.Should().HaveCount(2);
    }

    // ── Schema API ──────────────────────────────────────

    [Fact]
    public void Schema_lists_nexus_types_and_roles()
    {
        using var db = YatagarasuDatabase.Open(_path);
        using (var tx = db.BeginWriteTransaction())
        {
            var a = tx.CreateVertex("A");
            var b = tx.CreateVertex("B");
            tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
            tx.CreateNexus("Event", [new("Actor", a), new("Target", b)]);
            tx.Commit();
        }

        db.Schema.ListNexusTypes().Should().BeEquivalentTo(["Fact", "Event"]);
        db.Schema.ListRoles().Should().BeEquivalentTo(["Subject", "Object", "Actor", "Target"]);
    }

    [Fact]
    public void Schema_TryGetNexusTypeId_works()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var typeId = db.EditSchema(schema => schema.GetOrCreateNexusType("MyType"));
        db.Schema.TryGetNexusTypeId("MyType", out var found).Should().BeTrue();
        found.Should().Be(typeId);

        db.Schema.TryGetNexusTypeId("Unknown", out _).Should().BeFalse();
    }

    // ── helpers ──────────────────────────────────────────

    private static List<NexusMember> Collect(NexusMemberEnumerator e)
    {
        var list = new List<NexusMember>();
        while (e.MoveNext()) list.Add(e.Current);
        return list;
    }

    private static List<long> CollectIds(NexusIdEnumerator e)
    {
        var list = new List<long>();
        while (e.MoveNext()) list.Add(e.Current.Sequence);
        return list;
    }
}
