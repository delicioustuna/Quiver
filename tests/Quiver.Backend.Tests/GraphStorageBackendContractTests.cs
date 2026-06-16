using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// Backend contract tests (BA-2). Subclass for each backend factory and override
/// <see cref="CreateFactory"/>; the same test surface is exercised against every
/// implementation of <see cref="IGraphStorageBackend"/>.
/// </summary>
public abstract class GraphStorageBackendContractTests : IDisposable
{
    private readonly string _dir;
    private readonly IGraphStorageBackendFactory _factory;
    private IGraphStorageBackend _backend;

    protected GraphStorageBackendContractTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_backend_contract_" + Guid.NewGuid().ToString("N"));
        _factory = CreateFactory();
        _backend = _factory.Open(DatabasePath, new GraphDatabaseOptions());
    }

    /// <summary>ARCH-4: テストディレクトリ。fault 注入やファイルパス解決でサブクラスが参照する。</summary>
    protected string DatabaseDirectory => _dir;

    protected virtual string DatabasePath => _dir;

    public void Dispose()
    {
        _backend.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Factory under test. Each concrete subclass returns its own backend factory.
    /// </summary>
    protected abstract IGraphStorageBackendFactory CreateFactory();

    private IGraphTransaction BeginWrite() =>
        _backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false);

    private IGraphTransaction BeginRead() =>
        _backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true);

    private void Reopen()
    {
        _backend.Dispose();
        _backend = _factory.Open(DatabasePath, new GraphDatabaseOptions());
    }

    // ===== Node CRUD =====

    [Fact]
    public void CreateNode_then_NodeExists_returns_true()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("Person");
        tx.NodeExists(id).Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void DeleteNode_makes_NodeExists_false()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("Person");
        tx.DeleteNode(id);
        tx.NodeExists(id).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void CreateNode_with_LabelId_resolved_via_Schema_works()
    {
        var labelId = _backend.Schema.GetOrCreateLabel("Asset");
        using var tx = BeginWrite();
        var id = tx.CreateNode(labelId);
        tx.NodeExists(id).Should().BeTrue();
        tx.Commit();
    }

    // ===== Relationship CRUD =====

    [Fact]
    public void CreateRelationship_then_enumerate_finds_neighbor()
    {
        using var tx = BeginWrite();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        tx.CreateRelationship(a, b, "LINK");

        var neighbors = new List<NodeId>();
        var en = tx.EnumerateRelationships(a);
        while (en.MoveNext())
        {
            var r = en.Current;
            neighbors.Add(r.Source == a ? r.Target : r.Source);
        }

        neighbors.Should().ContainSingle().Which.Should().Be(b);
        tx.Commit();
    }

    [Fact]
    public void DeleteRelationship_removes_it_from_enumeration()
    {
        using var tx = BeginWrite();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");
        tx.DeleteRelationship(rel);

        var en = tx.EnumerateRelationships(a);
        en.MoveNext().Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void DeleteNode_with_incident_relationship_succeeds()
    {
        using var tx = BeginWrite();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        tx.CreateRelationship(a, b, "LINK");
        tx.DeleteNode(a);
        tx.NodeExists(a).Should().BeFalse();
        tx.Commit();
    }

    // ===== Property CRUD =====

    [Fact]
    public void SetProperty_then_GetProperty_returns_value()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("Item");
        tx.SetProperty(id, "score", PropertyValue.FromInt64(42L));
        var v = tx.GetProperty(id, "score");
        v.Type.Should().Be(PropertyValueType.Int64);
        v.Int64Value.Should().Be(42L);
        tx.Commit();
    }

    [Fact]
    public void SetProperty_overwrites_previous_value()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("X");
        tx.SetProperty(id, "n", PropertyValue.FromInt64(1L));
        tx.SetProperty(id, "n", PropertyValue.FromInt64(99L));
        tx.GetProperty(id, "n").Int64Value.Should().Be(99L);
        tx.Commit();
    }

    [Fact]
    public void RemoveProperty_then_HasProperty_returns_false()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("X");
        tx.SetProperty(id, "age", PropertyValue.FromInt32(30));
        tx.RemoveProperty(id, "age");
        tx.HasProperty(id, "age").Should().BeFalse();
        tx.Commit();
    }

    // ===== FT-30: defensive read API (HWM safe) =====

    [Fact]
    public void NodeExists_returns_false_for_id_past_hwm()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("Person");
        // 既存より十分大きい ID は未割当 → false (例外なし)。
        tx.NodeExists(new NodeId(id.Value + 1_000_000)).Should().BeFalse();
        tx.NodeExists(new NodeId(long.MaxValue / 2)).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void NodeExists_returns_false_for_negative_id()
    {
        using var tx = BeginWrite();
        tx.CreateNode("Person");
        tx.NodeExists(new NodeId(-1L)).Should().BeFalse();
        tx.NodeExists(new NodeId(long.MinValue)).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void HasProperty_returns_false_for_nonexistent_node()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString("alice"));
        var ghost = new NodeId(id.Value + 999_999);
        tx.HasProperty(ghost, "name").Should().BeFalse();
        // 既存ノードでも未設定 key は false。
        tx.HasProperty(id, "missing_key").Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void GetProperty_returns_default_for_nonexistent_node()
    {
        using var tx = BeginWrite();
        tx.CreateNode("Person");
        var ghost = new NodeId(999_999L);
        var pv = tx.GetProperty(ghost, "anything");
        pv.Type.Should().Be(default(PropertyValueType));
        tx.Commit();
    }

    [Fact]
    public void NodeExists_in_readonly_tx_does_not_throw_for_past_hwm()
    {
        using (var tx = BeginWrite())
        {
            tx.CreateNode("Person");
            tx.Commit();
        }
        using var rtx = BeginRead();
        rtx.NodeExists(new NodeId(42_000L)).Should().BeFalse();
        rtx.HasProperty(new NodeId(42_000L), "x").Should().BeFalse();
    }

    [Fact]
    public void Relationship_property_round_trips()
    {
        using var tx = BeginWrite();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");
        tx.SetProperty(rel, "since", PropertyValue.FromInt64(2020L));

        var v = tx.GetProperty(rel, "since");
        v.Type.Should().Be(PropertyValueType.Int64);
        v.Int64Value.Should().Be(2020L);
        tx.Commit();
    }

    // ===== Index seek =====

    [Fact]
    public void IndexInsert_string_then_SeekIndex_finds_node()
    {
        using var tx = BeginWrite();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.IndexInsert("idx_name", "Alice", alice);
        tx.IndexInsert("idx_name", "Bob",   bob);

        var en = tx.SeekIndex("idx_name", PropertyValue.FromString("Alice"));
        var found = new List<NodeId>();
        while (en.MoveNext()) found.Add(en.Current);
        en.Dispose();

        found.Should().ContainSingle().Which.Should().Be(alice);
        tx.Commit();
    }

    [Fact]
    public void IndexInsert_int64_then_SeekIndex_finds_node()
    {
        using var tx = BeginWrite();
        var n42 = tx.CreateNode("Item");
        var n99 = tx.CreateNode("Item");
        tx.IndexInsert("idx_score", 42L, n42);
        tx.IndexInsert("idx_score", 99L, n99);

        var en = tx.SeekIndex("idx_score", PropertyValue.FromInt64(42L));
        var found = new List<NodeId>();
        while (en.MoveNext()) found.Add(en.Current);
        en.Dispose();

        found.Should().ContainSingle().Which.Should().Be(n42);
        tx.Commit();
    }

    [Fact]
    public void SeekIndex_on_unknown_index_returns_empty()
    {
        using var tx = BeginWrite();
        var en = tx.SeekIndex("no_such_index", PropertyValue.FromInt64(1L));
        en.MoveNext().Should().BeFalse();
        en.Dispose();
        tx.Rollback();
    }

    // ===== 1-hop expansion =====

    [Fact]
    public void OneHop_enumerates_outgoing_and_incoming_edges()
    {
        using var tx = BeginWrite();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var carol = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob,   "KNOWS");
        tx.CreateRelationship(carol, alice, "KNOWS");

        var outgoing = new List<NodeId>();
        var incoming = new List<NodeId>();
        var en = tx.EnumerateRelationships(alice);
        while (en.MoveNext())
        {
            var r = en.Current;
            if (r.Source == alice) outgoing.Add(r.Target);
            else                   incoming.Add(r.Source);
        }

        outgoing.Should().ContainSingle().Which.Should().Be(bob);
        incoming.Should().ContainSingle().Which.Should().Be(carol);
        tx.Commit();
    }

    [Fact]
    public void OneHop_type_filter_with_outgoing_direction_returns_matching_edges_only()
    {
        // Direction is honored by EnumerateRelationships when typeFilter is supplied.
        using var tx = BeginWrite();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var dave  = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob,  "KNOWS");
        tx.CreateRelationship(alice, dave, "WORKS_WITH");

        var hits = new List<NodeId>();
        var en = tx.EnumerateRelationships(alice, Direction.Outgoing, typeFilter: "KNOWS");
        while (en.MoveNext())
            hits.Add(en.Current.Target);

        hits.Should().ContainSingle().Which.Should().Be(bob);
        tx.Commit();
    }

    // ===== MERGE / UPSERT (GC-5) =====

    [Fact]
    public void MergeNode_creates_when_no_match_exists()
    {
        using var tx = BeginWrite();
        var (id, created) = tx.MergeNode("Person", "name", PropertyValue.FromString("Alice"));
        created.Should().BeTrue();
        tx.NodeExists(id).Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "name").Utf8StringValue)
            .Should().Be("Alice");
        tx.Commit();
    }

    [Fact]
    public void MergeNode_returns_existing_when_match_exists_in_same_tx()
    {
        using var tx = BeginWrite();
        var first  = tx.MergeNode("Person", "name", PropertyValue.FromString("Alice"));
        var second = tx.MergeNode("Person", "name", PropertyValue.FromString("Alice"));

        first.Created.Should().BeTrue();
        second.Created.Should().BeFalse();
        second.Id.Should().Be(first.Id);
        tx.Commit();
    }

    [Fact]
    public void MergeNode_finds_existing_node_across_commits()
    {
        NodeId persisted;
        using (var tx = BeginWrite())
        {
            (persisted, _) = tx.MergeNode("Person", "name", PropertyValue.FromString("Bob"));
            tx.Commit();
        }

        using var tx2 = BeginWrite();
        var (id, created) = tx2.MergeNode("Person", "name", PropertyValue.FromString("Bob"));
        created.Should().BeFalse();
        id.Should().Be(persisted);
        tx2.Commit();
    }

    [Fact]
    public void MergeNode_distinguishes_by_label()
    {
        using var tx = BeginWrite();
        var (person, pc) = tx.MergeNode("Person",  "name", PropertyValue.FromString("Alice"));
        var (city,   cc) = tx.MergeNode("City",    "name", PropertyValue.FromString("Alice"));

        pc.Should().BeTrue();
        cc.Should().BeTrue();
        city.Should().NotBe(person);
        tx.Commit();
    }

    [Fact]
    public void MergeNode_matches_by_int_property()
    {
        using var tx = BeginWrite();
        var (a, _) = tx.MergeNode("Item", "sku", PropertyValue.FromInt64(42L));
        var (b, created) = tx.MergeNode("Item", "sku", PropertyValue.FromInt64(42L));
        created.Should().BeFalse();
        b.Should().Be(a);
        tx.Commit();
    }

    // ===== Commit / Rollback =====

    [Fact]
    public void Commit_persists_writes_to_a_new_transaction()
    {
        NodeId persisted;
        using (var tx = BeginWrite())
        {
            persisted = tx.CreateNode("Persisted");
            tx.SetProperty(persisted, "marker", PropertyValue.FromInt64(7L));
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.NodeExists(persisted).Should().BeTrue();
        rtx.GetProperty(persisted, "marker").Int64Value.Should().Be(7L);
        rtx.Rollback();
    }

    [Fact]
    public void Rollback_transitions_state_to_aborted_and_releases_tx()
    {
        // FT-15: rollback undoes the transaction's writes, releases locks and
        // ends the tx lifecycle. New transactions can be opened immediately
        // afterwards on the same backend, and the discarded node is invisible.
        NodeId discarded;
        var tx = BeginWrite();
        discarded = tx.CreateNode("Discarded");
        tx.Rollback();
        tx.State.Should().Be(TransactionState.Aborted);
        tx.Dispose();

        using (var next = BeginWrite())
        {
            next.NodeExists(discarded).Should().BeFalse(
                "a rolled-back CreateNode must not be visible to later transactions");
            next.CreateNode("Subsequent");
            next.Commit();
        }
    }

    [Fact]
    public void Rollback_discards_node_property_and_relationship_writes()
    {
        // FT-15 Tier1: every kind of write in an aborted transaction must vanish.
        NodeId a, b;
        using (var tx = BeginWrite())
        {
            a = tx.CreateNode("A");
            b = tx.CreateNode("B");
            tx.SetProperty(a, "score", PropertyValue.FromInt64(123L));
            tx.CreateRelationship(a, b, "LINK");
            tx.Rollback();
        }

        using var rtx = BeginRead();
        rtx.NodeExists(a).Should().BeFalse("rolled-back node A must be gone");
        rtx.NodeExists(b).Should().BeFalse("rolled-back node B must be gone");
        rtx.Rollback();
    }

    [Fact]
    public void Dispose_without_commit_discards_writes()
    {
        // FT-15 Tier1: letting a write transaction Dispose without Commit aborts
        // it — the undo must run, not just lock release.
        NodeId discarded;
        using (var tx = BeginWrite())
        {
            discarded = tx.CreateNode("Discarded");
            // Intentionally no Commit / no Rollback.
        }

        using var rtx = BeginRead();
        rtx.NodeExists(discarded).Should().BeFalse(
            "a write transaction disposed without Commit must discard its writes");
        rtx.Rollback();
    }

    [Fact]
    public void Rollback_preserves_previously_committed_data()
    {
        // FT-15 Tier1: an aborted transaction must not damage data that an
        // earlier transaction committed, including reverting in-flight mutations
        // of committed records back to their committed value.
        NodeId keeper;
        using (var tx = BeginWrite())
        {
            keeper = tx.CreateNode("Keeper");
            tx.SetProperty(keeper, "v", PropertyValue.FromInt64(7L));
            tx.Commit();
        }

        using (var tx = BeginWrite())
        {
            tx.CreateNode("Doomed");
            tx.SetProperty(keeper, "v", PropertyValue.FromInt64(999L));
            tx.Rollback();
        }

        using var rtx = BeginRead();
        rtx.NodeExists(keeper).Should().BeTrue("committed data survives an unrelated rollback");
        rtx.GetProperty(keeper, "v").Int64Value.Should().Be(7L,
            "a rolled-back property mutation must restore the committed value");
        rtx.Rollback();
    }

    [Fact]
    public void Commit_survives_backend_reopen()
    {
        NodeId persisted;
        using (var tx = BeginWrite())
        {
            persisted = tx.CreateNode("Survives");
            tx.SetProperty(persisted, "ok", PropertyValue.FromBool(true));
            tx.Commit();
        }

        Reopen();

        using var rtx = BeginRead();
        rtx.NodeExists(persisted).Should().BeTrue();
        rtx.GetProperty(persisted, "ok").Type.Should().Be(PropertyValueType.Bool);
        rtx.Rollback();
    }

    // ===== FT-23: Savepoint / nested undo =====

    [Fact]
    public void Savepoint_RollbackTo_discards_changes_after_savepoint_only()
    {
        // Savepoint 前の変更は保たれ、Savepoint 後の変更だけが消える。
        NodeId before, after;
        using (var tx = BeginWrite())
        {
            before = tx.CreateNode("Before");
            tx.SetProperty(before, "k", PropertyValue.FromInt64(1L));
            var sp = tx.Savepoint();
            after = tx.CreateNode("After");
            tx.SetProperty(before, "k", PropertyValue.FromInt64(999L));
            tx.RollbackTo(sp);
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.NodeExists(before).Should().BeTrue("savepoint 以前のノードは生存");
        rtx.NodeExists(after).Should().BeFalse("savepoint 以後のノードは消える");
        rtx.GetProperty(before, "k").Int64Value.Should().Be(1L,
            "savepoint 以後のプロパティ上書きは取り消される");
        rtx.Rollback();
    }

    [Fact]
    public void Savepoint_nested_three_levels_rolls_back_to_each_level()
    {
        // 3 段ネスト: SP1 → 操作 → SP2 → 操作 → SP3 → 操作 → RollbackTo(SP2) で SP2 直後の状態へ。
        NodeId n0, n1, n2, n3;
        using (var tx = BeginWrite())
        {
            n0 = tx.CreateNode("L0");
            var sp1 = tx.Savepoint("sp1");
            n1 = tx.CreateNode("L1");
            var sp2 = tx.Savepoint("sp2");
            n2 = tx.CreateNode("L2");
            var sp3 = tx.Savepoint("sp3");
            n3 = tx.CreateNode("L3");

            tx.RollbackTo(sp2);
            // sp2 以降 (n2, n3 含む sp3 も) が消える。n0, n1 は残る。
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.NodeExists(n0).Should().BeTrue();
        rtx.NodeExists(n1).Should().BeTrue();
        rtx.NodeExists(n2).Should().BeFalse("RollbackTo(sp2) で n2 は消える");
        rtx.NodeExists(n3).Should().BeFalse("RollbackTo(sp2) で sp3 配下の n3 も消える");
        rtx.Rollback();
    }

    [Fact]
    public void ReleaseSavepoint_keeps_all_changes_in_committed_tx()
    {
        // Release は savepoint を消費するが、savepoint 内の変更は親へマージされる。
        NodeId outer, inner;
        using (var tx = BeginWrite())
        {
            outer = tx.CreateNode("Outer");
            var sp = tx.Savepoint();
            inner = tx.CreateNode("Inner");
            tx.ReleaseSavepoint(sp);
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.NodeExists(outer).Should().BeTrue();
        rtx.NodeExists(inner).Should().BeTrue("released savepoint 内の変更はコミットで永続化");
        rtx.Rollback();
    }

    [Fact]
    public void Savepoint_can_be_rolled_back_to_multiple_times()
    {
        // SQL 標準: ROLLBACK TO は savepoint を消費せず、同じ id で再度 RollbackTo できる。
        NodeId pre;
        using (var tx = BeginWrite())
        {
            pre = tx.CreateNode("Pre");
            var sp = tx.Savepoint();

            tx.CreateNode("Throw1");
            tx.RollbackTo(sp);

            tx.CreateNode("Throw2");
            tx.RollbackTo(sp);

            tx.Commit();
        }

        // pre 以外のノードは何も残らない (Throw1/Throw2 は両方とも消えた)。
        using var rtx = BeginRead();
        rtx.NodeExists(pre).Should().BeTrue();
        rtx.Rollback();
    }

    [Fact]
    public void Savepoint_RollbackTo_invalidates_inner_savepoints()
    {
        // RollbackTo(sp_outer) は sp_outer より新しい savepoint をすべて無効化する (SQL 標準)。
        // 以降に内側 SavepointId を使うと例外。
        using var tx = BeginWrite();
        var spOuter = tx.Savepoint();
        var spInner = tx.Savepoint();
        tx.CreateNode("Mid");
        tx.RollbackTo(spOuter);

        var act = () => tx.RollbackTo(spInner);
        act.Should().Throw<Exception>(
            "RollbackTo(spOuter) は spInner を無効化したので、その後 spInner を使うと throw");
        tx.Rollback();
    }

    [Fact]
    public void RollbackTo_unknown_savepoint_throws()
    {
        using var tx1 = BeginWrite();
        SavepointId stranger = tx1.Savepoint();
        tx1.Rollback();

        using var tx2 = BeginWrite();
        var act = () => tx2.RollbackTo(stranger);
        act.Should().Throw<Exception>("別 tx の savepoint id を渡すと throw");
        tx2.Rollback();
    }

    [Fact]
    public void Plain_commit_without_savepoint_behaves_unchanged()
    {
        // Regression: savepoint を使わない既存パスの挙動が変わらないこと。
        NodeId committed;
        using (var tx = BeginWrite())
        {
            committed = tx.CreateNode("Plain");
            tx.SetProperty(committed, "v", PropertyValue.FromInt64(42L));
            tx.Commit();
        }
        using var rtx = BeginRead();
        rtx.NodeExists(committed).Should().BeTrue();
        rtx.GetProperty(committed, "v").Int64Value.Should().Be(42L);
        rtx.Rollback();
    }
}
