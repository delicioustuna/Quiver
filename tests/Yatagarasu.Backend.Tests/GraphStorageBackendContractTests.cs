using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Backend.Tests;

/// <summary>
/// backend の共通契約テスト。各 backend factory のサブクラスで
/// <see cref="CreateFactory"/> を override し、すべての
/// <see cref="IGraphStorageBackend"/> 実装に同じテストを適用する。
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
            "yatagarasu_backend_contract_" + Guid.NewGuid().ToString("N"));
        _factory = CreateFactory();
        _backend = _factory.Open(DatabasePath, new YatagarasuDatabaseOptions());
    }

    /// <summary>テストディレクトリ。fault 注入やファイルパス解決でサブクラスが参照する。</summary>
    protected string DatabaseDirectory => _dir;

    protected virtual string DatabasePath => _dir;

    /// <summary>バックエンドを再オープンしたときにコミット済みデータが残るか。</summary>
    protected virtual bool SupportsPersistence => true;

    public void Dispose()
    {
        _backend.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// テスト対象の factory。具象サブクラスは対応する backend factory を返す。
    /// </summary>
    private protected abstract IGraphStorageBackendFactory CreateFactory();

    private IWriteTransaction BeginWrite() =>
        _backend.BeginWriteTransaction();

    private IReadTransaction BeginRead() =>
        _backend.BeginReadTransaction();

    private void Reopen()
    {
        _backend.Dispose();
        _backend = _factory.Open(DatabasePath, new YatagarasuDatabaseOptions());
    }

    // ===== Vertex CRUD =====

    [Fact]
    public void CreateVertex_then_VertexExists_returns_true()
    {
        using var tx = BeginWrite();
        var id = tx.CreateVertex("Person");
        tx.VertexExists(id).Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void DeleteVertex_makes_VertexExists_false()
    {
        using var tx = BeginWrite();
        var id = tx.CreateVertex("Person");
        tx.DeleteVertex(id);
        tx.VertexExists(id).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void CreateVertex_with_LabelId_resolved_via_Schema_works()
    {
        using var tx = BeginWrite();
        var labelId = tx.EditSchema.GetOrCreateLabel("Asset");
        var id = tx.CreateVertex(labelId);
        tx.VertexExists(id).Should().BeTrue();
        tx.Commit();
    }

    // ===== Edge CRUD =====

    [Fact]
    public void CreateEdge_then_enumerate_finds_neighbor()
    {
        using var tx = BeginWrite();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        tx.CreateEdge(a, b, "LINK");

        var neighbors = new List<VertexId>();
        var en = tx.EnumerateEdges(a);
        while (en.MoveNext())
        {
            var r = en.Current;
            neighbors.Add(r.Source == a ? r.Target : r.Source);
        }

        neighbors.Should().ContainSingle().Which.Should().Be(b);
        tx.Commit();
    }

    [Fact]
    public void DeleteEdge_removes_it_from_enumeration()
    {
        using var tx = BeginWrite();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "LINK");
        tx.DeleteEdge(edge);

        var en = tx.EnumerateEdges(a);
        en.MoveNext().Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void DeleteVertex_with_incident_edge_succeeds()
    {
        using var tx = BeginWrite();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        tx.CreateEdge(a, b, "LINK");
        tx.DeleteVertex(a);
        tx.VertexExists(a).Should().BeFalse();
        tx.Commit();
    }

    // ===== プロパティ CRUD =====

    [Fact]
    public void SetProperty_then_GetProperty_returns_value()
    {
        using var tx = BeginWrite();
        var id = tx.CreateVertex("Item");
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
        var id = tx.CreateVertex("X");
        tx.SetProperty(id, "n", PropertyValue.FromInt64(1L));
        tx.SetProperty(id, "n", PropertyValue.FromInt64(99L));
        tx.GetProperty(id, "n").Int64Value.Should().Be(99L);
        tx.Commit();
    }

    [Fact]
    public void RemoveProperty_then_HasProperty_returns_false()
    {
        using var tx = BeginWrite();
        var id = tx.CreateVertex("X");
        tx.SetProperty(id, "age", PropertyValue.FromInt32(30));
        tx.RemoveProperty(id, "age");
        tx.HasProperty(id, "age").Should().BeFalse();
        tx.Commit();
    }

    // ===== HWM 境界を安全に扱う防御的読み取り API =====

    [Fact]
    public void VertexExists_returns_false_for_id_past_hwm()
    {
        using var tx = BeginWrite();
        var id = tx.CreateVertex("Person");
        // 既存より十分大きい ID は未割当 → false (例外なし)。
        tx.VertexExists(new VertexId(id.Value + 1_000_000)).Should().BeFalse();
        tx.VertexExists(new VertexId(long.MaxValue / 2)).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void VertexExists_returns_false_for_negative_id()
    {
        using var tx = BeginWrite();
        tx.CreateVertex("Person");
        tx.VertexExists(new VertexId(-1L)).Should().BeFalse();
        tx.VertexExists(new VertexId(long.MinValue)).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void HasProperty_returns_false_for_nonexistent_vertex()
    {
        using var tx = BeginWrite();
        var id = tx.CreateVertex("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString("alice"));
        var ghost = new VertexId(id.Value + 999_999);
        tx.HasProperty(ghost, "name").Should().BeFalse();
        // 既存Vertexでも未設定 key は false。
        tx.HasProperty(id, "missing_key").Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void GetProperty_returns_default_for_nonexistent_vertex()
    {
        using var tx = BeginWrite();
        tx.CreateVertex("Person");
        var ghost = new VertexId(999_999L);
        var pv = tx.GetProperty(ghost, "anything");
        pv.Type.Should().Be(default(PropertyValueType));
        tx.Commit();
    }

    [Fact]
    public void VertexExists_in_readonly_tx_does_not_throw_for_past_hwm()
    {
        using (var tx = BeginWrite())
        {
            tx.CreateVertex("Person");
            tx.Commit();
        }
        using var rtx = BeginRead();
        rtx.VertexExists(new VertexId(42_000L)).Should().BeFalse();
        rtx.HasProperty(new VertexId(42_000L), "x").Should().BeFalse();
    }

    [Fact]
    public void Edge_property_round_trips()
    {
        using var tx = BeginWrite();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "LINK");
        tx.SetProperty(edge, "since", PropertyValue.FromInt64(2020L));

        var v = tx.GetProperty(edge, "since");
        v.Type.Should().Be(PropertyValueType.Int64);
        v.Int64Value.Should().Be(2020L);
        tx.Commit();
    }

    // ===== インデックス検索 =====

    [Fact]
    public void SetProperty_string_then_SeekIndex_finds_vertex()
    {
        using var tx = BeginWrite();
        tx.EditSchema.CreateIndex(new ScalarIndexDefinition(
            "idx_name",
            new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"),
            IndexKind.StringEquality));
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
        tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));

        var en = tx.SeekIndex("idx_name", PropertyValue.FromString("Alice"));
        var found = new List<EntityRef>();
        while (en.MoveNext()) found.Add(en.Current);
        en.Dispose();

        found.Should().ContainSingle().Which.Should().Be(EntityRef.From(alice));
        tx.Commit();
    }

    [Fact]
    public void SetProperty_int64_then_SeekIndex_finds_vertex()
    {
        using var tx = BeginWrite();
        tx.EditSchema.CreateIndex(new ScalarIndexDefinition(
            "idx_score",
            new PropertyTarget(PropertyOwnerKind.Vertex, "score", "Item"),
            IndexKind.Int64Equality));
        var n42 = tx.CreateVertex("Item");
        var n99 = tx.CreateVertex("Item");
        tx.SetProperty(n42, "score", PropertyValue.FromInt64(42L));
        tx.SetProperty(n99, "score", PropertyValue.FromInt64(99L));

        var en = tx.SeekIndex("idx_score", PropertyValue.FromInt64(42L));
        var found = new List<EntityRef>();
        while (en.MoveNext()) found.Add(en.Current);
        en.Dispose();

        found.Should().ContainSingle().Which.Should().Be(EntityRef.From(n42));
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

    // ===== 1-hop 展開 =====

    [Fact]
    public void OneHop_enumerates_outgoing_and_incoming_edges()
    {
        using var tx = BeginWrite();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var carol = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob,   "KNOWS");
        tx.CreateEdge(carol, alice, "KNOWS");

        var outgoing = new List<VertexId>();
        var incoming = new List<VertexId>();
        var en = tx.EnumerateEdges(alice);
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
        // typeFilter 指定時も EnumerateEdges は Direction を尊重する。
        using var tx = BeginWrite();
        var alice = tx.CreateVertex("Person");
        var bob   = tx.CreateVertex("Person");
        var dave  = tx.CreateVertex("Person");
        tx.CreateEdge(alice, bob,  "KNOWS");
        tx.CreateEdge(alice, dave, "WORKS_WITH");

        var hits = new List<VertexId>();
        var en = tx.EnumerateEdges(alice, Direction.Outgoing, typeFilter: "KNOWS");
        while (en.MoveNext())
            hits.Add(en.Current.Target);

        hits.Should().ContainSingle().Which.Should().Be(bob);
        tx.Commit();
    }

    // ===== MERGE / UPSERT =====

    [Fact]
    public void MergeVertex_creates_when_no_match_exists()
    {
        using var tx = BeginWrite();
        var (id, created) = tx.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));
        created.Should().BeTrue();
        tx.VertexExists(id).Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "name").Utf8StringValue)
            .Should().Be("Alice");
        tx.Commit();
    }

    [Fact]
    public void MergeVertex_returns_existing_when_match_exists_in_same_tx()
    {
        using var tx = BeginWrite();
        var first  = tx.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));
        var second = tx.MergeVertex("Person", "name", PropertyValue.FromString("Alice"));

        first.Created.Should().BeTrue();
        second.Created.Should().BeFalse();
        second.Id.Should().Be(first.Id);
        tx.Commit();
    }

    [Fact]
    public void MergeVertex_finds_existing_vertex_across_commits()
    {
        VertexId persisted;
        using (var tx = BeginWrite())
        {
            (persisted, _) = tx.MergeVertex("Person", "name", PropertyValue.FromString("Bob"));
            tx.Commit();
        }

        using var tx2 = BeginWrite();
        var (id, created) = tx2.MergeVertex("Person", "name", PropertyValue.FromString("Bob"));
        created.Should().BeFalse();
        id.Should().Be(persisted);
        tx2.Commit();
    }

    [Fact]
    public void MergeVertex_distinguishes_by_label()
    {
        using var tx = BeginWrite();
        var (person, pc) = tx.MergeVertex("Person",  "name", PropertyValue.FromString("Alice"));
        var (city,   cc) = tx.MergeVertex("City",    "name", PropertyValue.FromString("Alice"));

        pc.Should().BeTrue();
        cc.Should().BeTrue();
        city.Should().NotBe(person);
        tx.Commit();
    }

    [Fact]
    public void MergeVertex_matches_by_int_property()
    {
        using var tx = BeginWrite();
        var (a, _) = tx.MergeVertex("Item", "sku", PropertyValue.FromInt64(42L));
        var (b, created) = tx.MergeVertex("Item", "sku", PropertyValue.FromInt64(42L));
        created.Should().BeFalse();
        b.Should().Be(a);
        tx.Commit();
    }

    // ===== Commit / Rollback =====

    [Fact]
    public void Commit_persists_writes_to_a_new_transaction()
    {
        VertexId persisted;
        using (var tx = BeginWrite())
        {
            persisted = tx.CreateVertex("Persisted");
            tx.SetProperty(persisted, "marker", PropertyValue.FromInt64(7L));
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.VertexExists(persisted).Should().BeTrue();
        rtx.GetProperty(persisted, "marker").Int64Value.Should().Be(7L);
    }

    [Fact]
    public void Rollback_transitions_state_to_aborted_and_releases_tx()
    {
        // rollback は書き込みを取り消して lock を解放し、トランザクションを終了する。
        // 直後に同じ backend で新しいトランザクションを開始でき、
        // 破棄されたVertexはそのトランザクションから見えない。
        VertexId discarded;
        var tx = BeginWrite();
        discarded = tx.CreateVertex("Discarded");
        tx.Rollback();
        tx.State.Should().Be(TransactionState.Aborted);
        tx.Dispose();

        using (var next = BeginWrite())
        {
            next.VertexExists(discarded).Should().BeFalse(
                "a rolled-back CreateVertex must not be visible to later transactions");
            next.CreateVertex("Subsequent");
            next.Commit();
        }
    }

    [Fact]
    public void Rollback_discards_vertex_property_and_edge_writes()
    {
        // abort されたトランザクションのあらゆる書き込みは消失しなければならない。
        VertexId a, b;
        using (var tx = BeginWrite())
        {
            a = tx.CreateVertex("A");
            b = tx.CreateVertex("B");
            tx.SetProperty(a, "score", PropertyValue.FromInt64(123L));
            tx.CreateEdge(a, b, "LINK");
            tx.Rollback();
        }

        using var rtx = BeginRead();
        rtx.VertexExists(a).Should().BeFalse("rolled-back vertex A must be gone");
        rtx.VertexExists(b).Should().BeFalse("rolled-back vertex B must be gone");
    }

    [Fact]
    public void Dispose_without_commit_discards_writes()
    {
        // 書き込みトランザクションを Commit せず Dispose すると abort する。
        // lock 解放だけでなく undo も実行されなければならない。
        VertexId discarded;
        using (var tx = BeginWrite())
        {
            discarded = tx.CreateVertex("Discarded");
            // 意図的に Commit も Rollback もしない。
        }

        using var rtx = BeginRead();
        rtx.VertexExists(discarded).Should().BeFalse(
            "a write transaction disposed without Commit must discard its writes");
    }

    [Fact]
    public void Rollback_preserves_previously_committed_data()
    {
        // abort されたトランザクションは、先行トランザクションのコミット済みデータを
        // 損傷してはならない。コミット済みレコードへの処理中の変更も元の値へ戻す。
        VertexId keeper;
        using (var tx = BeginWrite())
        {
            keeper = tx.CreateVertex("Keeper");
            tx.SetProperty(keeper, "v", PropertyValue.FromInt64(7L));
            tx.Commit();
        }

        using (var tx = BeginWrite())
        {
            tx.CreateVertex("Doomed");
            tx.SetProperty(keeper, "v", PropertyValue.FromInt64(999L));
            tx.Rollback();
        }

        using var rtx = BeginRead();
        rtx.VertexExists(keeper).Should().BeTrue("committed data survives an unrelated rollback");
        rtx.GetProperty(keeper, "v").Int64Value.Should().Be(7L,
            "a rolled-back property mutation must restore the committed value");
    }

    [Fact]
    public void Commit_survives_backend_reopen()
    {
        VertexId persisted;
        using (var tx = BeginWrite())
        {
            persisted = tx.CreateVertex("Survives");
            tx.SetProperty(persisted, "ok", PropertyValue.FromBool(true));
            tx.Commit();
        }

        Reopen();

        using var rtx = BeginRead();
        if (SupportsPersistence)
        {
            rtx.VertexExists(persisted).Should().BeTrue();
            rtx.GetProperty(persisted, "ok").Type.Should().Be(PropertyValueType.Bool);
        }
        else
        {
            rtx.VertexExists(persisted).Should().BeFalse(
                "非永続バックエンドは再オープン時に空の状態へ戻る");
        }
    }

    // ===== Savepoint / ネストした undo =====

    [Fact]
    public void Savepoint_RollbackTo_discards_changes_after_savepoint_only()
    {
        // Savepoint 前の変更は保たれ、Savepoint 後の変更だけが消える。
        VertexId before, after;
        using (var tx = BeginWrite())
        {
            before = tx.CreateVertex("Before");
            tx.SetProperty(before, "k", PropertyValue.FromInt64(1L));
            var sp = tx.Savepoint();
            after = tx.CreateVertex("After");
            tx.SetProperty(before, "k", PropertyValue.FromInt64(999L));
            tx.RollbackTo(sp);
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.VertexExists(before).Should().BeTrue("savepoint 以前のVertexは生存");
        rtx.VertexExists(after).Should().BeFalse("savepoint 以後のVertexは消える");
        rtx.GetProperty(before, "k").Int64Value.Should().Be(1L,
            "savepoint 以後のプロパティ上書きは取り消される");
    }

    [Fact]
    public void Savepoint_nested_three_levels_rolls_back_to_each_level()
    {
        // 3 段ネスト: SP1 → 操作 → SP2 → 操作 → SP3 → 操作 → RollbackTo(SP2) で SP2 直後の状態へ。
        VertexId n0, n1, n2, n3;
        using (var tx = BeginWrite())
        {
            n0 = tx.CreateVertex("L0");
            var sp1 = tx.Savepoint("sp1");
            n1 = tx.CreateVertex("L1");
            var sp2 = tx.Savepoint("sp2");
            n2 = tx.CreateVertex("L2");
            var sp3 = tx.Savepoint("sp3");
            n3 = tx.CreateVertex("L3");

            tx.RollbackTo(sp2);
            // sp2 以降 (n2, n3 含む sp3 も) が消える。n0, n1 は残る。
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.VertexExists(n0).Should().BeTrue();
        rtx.VertexExists(n1).Should().BeTrue();
        rtx.VertexExists(n2).Should().BeFalse("RollbackTo(sp2) で n2 は消える");
        rtx.VertexExists(n3).Should().BeFalse("RollbackTo(sp2) で sp3 配下の n3 も消える");
    }

    [Fact]
    public void ReleaseSavepoint_keeps_all_changes_in_committed_tx()
    {
        // Release は savepoint を消費するが、savepoint 内の変更は親へマージされる。
        VertexId outer, inner;
        using (var tx = BeginWrite())
        {
            outer = tx.CreateVertex("Outer");
            var sp = tx.Savepoint();
            inner = tx.CreateVertex("Inner");
            tx.ReleaseSavepoint(sp);
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.VertexExists(outer).Should().BeTrue();
        rtx.VertexExists(inner).Should().BeTrue("released savepoint 内の変更はコミットで永続化");
    }

    [Fact]
    public void Savepoint_can_be_rolled_back_to_multiple_times()
    {
        // SQL 標準: ROLLBACK TO は savepoint を消費せず、同じ id で再度 RollbackTo できる。
        VertexId pre;
        using (var tx = BeginWrite())
        {
            pre = tx.CreateVertex("Pre");
            var sp = tx.Savepoint();

            tx.CreateVertex("Throw1");
            tx.RollbackTo(sp);

            tx.CreateVertex("Throw2");
            tx.RollbackTo(sp);

            tx.Commit();
        }

        // pre 以外のVertexは何も残らない (Throw1/Throw2 は両方とも消えた)。
        using var rtx = BeginRead();
        rtx.VertexExists(pre).Should().BeTrue();
    }

    [Fact]
    public void Savepoint_RollbackTo_invalidates_inner_savepoints()
    {
        // RollbackTo(sp_outer) は sp_outer より新しい savepoint をすべて無効化する (SQL 標準)。
        // 以降に内側 SavepointId を使うと例外。
        using var tx = BeginWrite();
        var spOuter = tx.Savepoint();
        var spInner = tx.Savepoint();
        tx.CreateVertex("Mid");
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
        VertexId committed;
        using (var tx = BeginWrite())
        {
            committed = tx.CreateVertex("Plain");
            tx.SetProperty(committed, "v", PropertyValue.FromInt64(42L));
            tx.Commit();
        }
        using var rtx = BeginRead();
        rtx.VertexExists(committed).Should().BeTrue();
        rtx.GetProperty(committed, "v").Int64Value.Should().Be(42L);
    }
}
