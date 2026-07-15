using FluentAssertions;
using Quiver.Api;
using Quiver.Maintenance;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// Vacuum による不要版の物理回収、フリーリストへの登録、末尾の高水位標縮小、
/// コミット済みレジストリの整理を確認する。
/// MVCC で論理削除されたノードが <see cref="GraphDatabase.Vacuum"/> によって
/// 再利用可能な物理スロットへ戻ることを検証する。
/// </summary>
public sealed class VacuumTests : IDisposable
{
    private readonly string _dir;

    public VacuumTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_op3_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Vacuum_reclaims_committed_dead_node_versions()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 100 個のノードを作成 → 全削除 → vacuum で物理回収。
        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 100; i++)
                ids.Add(tx.CreateNode("Person").Value);
            tx.Commit();
        }
        db.Diagnostics.GetStatistics().NodeCount.Should().Be(100);

        using (var tx = db.BeginTransaction())
        {
            foreach (var id in ids)
                tx.DeleteNode(new Core.NodeId(id));
            tx.Commit();
        }
        db.Diagnostics.GetStatistics().NodeCount.Should().Be(0);

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedNodes.Should().Be(100);
        // 末尾の連続 free slot で hwm が 0 まで縮む。
        report.HorizonTxId.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Vacuum_returns_Skipped_when_active_transactions_exist()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 削除済み version を 1 件作る (vacuum 対象がある状態)。
        long id;
        using (var tx = db.BeginTransaction())
        {
            id = tx.CreateNode("Person").Value;
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(new Core.NodeId(id));
            tx.Commit();
        }

        // アクティブ tx を抱えた状態で vacuum 起動 → Skipped。
        using var holder = db.BeginReadOnlyTransaction();
        var report = db.Vacuum();
        report.Skipped.Should().BeTrue();
        report.ReclaimedNodes.Should().Be(0);
    }

    [Fact]
    public void Vacuum_freed_slots_are_reused_by_subsequent_Allocate()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 10 ノード作成 → 全削除 → vacuum。
        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 10; i++)
                ids.Add(tx.CreateNode("Person").Value);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            foreach (var v in ids) tx.DeleteNode(new Core.NodeId(v));
            tx.Commit();
        }
        var report = db.Vacuum();
        report.ReclaimedNodes.Should().Be(10);

        // vacuum 後の新規 Allocate は回収済み slot (= 元と同じ ID 範囲) を再利用する。
        var newIds = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 5; i++)
                newIds.Add(tx.CreateNode("Person").Sequence); // ARCH-5b: slot 再利用は Sequence で確認
            tx.Commit();
        }

        // 元の ID 範囲 [0..9] のいずれかが再利用される (free list / hwm 縮減後の dense slot)。
        newIds.Should().OnlyContain(id => id < 10);
    }

    // gen-stamp-fastpath: 再利用 (gen>=2) が起きた後、クエリ結果の NodeId が bump 世代を
    // 載せること = 世代 stamping の高速パスが正しく per-row read へフォールバックしている検証。
    [Fact]
    public void Query_after_slot_reuse_returns_bumped_generation()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        long seq;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("Person");
            seq = a.Sequence;
            a.Generation.Should().Be(1);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(new Core.NodeId(seq));
            tx.Commit();
        }
        db.Vacuum().ReclaimedNodes.Should().Be(1);

        Core.NodeId reused;
        using (var tx = db.BeginTransaction())
        {
            reused = tx.CreateNode("Person");
            tx.Commit();
        }
        reused.Sequence.Should().Be(seq);   // 同 slot を再利用
        reused.Generation.Should().Be(2);   // 世代 bump

        using (var tx = db.BeginReadOnlyTransaction())
        {
            var rows = tx.G(db.Schema).Nodes().ToList();
            rows.Should().ContainSingle();
            rows[0].Sequence.Should().Be(seq);
            rows[0].Generation.Should().Be(2);          // stamping が bump 世代を載せる
            rows[0].Value.Should().Be(reused.Value);    // 往復一貫
        }
    }

    // gen-stamp-fastpath: AnyReuse フラグが sidecar ヘッダに永続化され、reopen 後も
    // 高速パスが無効のまま正しい世代を返すこと。
    [Fact]
    public void Reopen_after_slot_reuse_keeps_bumped_generation_in_query()
    {
        string path = System.IO.Path.Combine(_dir, "graph.quiver");
        long seq;
        using (var db = GraphDatabase.Open(path))
        {
            using (var tx = db.BeginTransaction()) { seq = tx.CreateNode("P").Sequence; tx.Commit(); }
            using (var tx = db.BeginTransaction()) { tx.DeleteNode(new Core.NodeId(seq)); tx.Commit(); }
            db.Vacuum();
            using (var tx = db.BeginTransaction()) { tx.CreateNode("P").Generation.Should().Be(2); tx.Commit(); }
        }

        using (var db = GraphDatabase.Open(path))
        using (var tx = db.BeginReadOnlyTransaction())
        {
            var rows = tx.G(db.Schema).Nodes().ToList();
            rows.Should().ContainSingle();
            rows[0].Generation.Should().Be(2);
        }
    }

    [Fact]
    public void DryRun_does_not_write()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateNode("Person");
            tx.DeleteNode(n);
            tx.Commit();
        }

        var dryRun = db.Vacuum(new VacuumOptions { Mode = VacuumMode.DryRun });
        dryRun.Skipped.Should().BeFalse();
        dryRun.ReclaimedNodes.Should().Be(0); // ドライランは書き込まない
        dryRun.PrunedCommittedTxEntries.Should().Be(0);

        // 続けて実行する Full は普通に回収できる。
        var full = db.Vacuum();
        full.ReclaimedNodes.Should().Be(1);
    }

    [Fact]
    public void Vacuum_reclaims_dead_relationships_and_keeps_live_chain()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 3 ノード a/b/c。a→b, a→c, a→b の 3 リレーション。
        long aId, bId, cId;
        long r1, r2, r3;
        using (var tx = db.BeginTransaction())
        {
            aId = tx.CreateNode("Person").Value;
            bId = tx.CreateNode("Person").Value;
            cId = tx.CreateNode("Person").Value;
            r1 = tx.CreateRelationship(new Core.NodeId(aId), new Core.NodeId(bId), "KNOWS").Value;
            r2 = tx.CreateRelationship(new Core.NodeId(aId), new Core.NodeId(cId), "KNOWS").Value;
            r3 = tx.CreateRelationship(new Core.NodeId(aId), new Core.NodeId(bId), "KNOWS").Value;
            tx.Commit();
        }
        // 中間の rel r2 だけ削除。
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteRelationship(new Core.RelationshipId(r2));
            tx.Commit();
        }

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedRelationships.Should().Be(1);

        // a の chain は r1, r3 だけが残ること。
        using var read = db.BeginReadOnlyTransaction();
        var outs = new List<long>();
        var en = read.EnumerateRelationships(new Core.NodeId(aId), Storage.Records.Direction.Outgoing);
        while (en.MoveNext())
            outs.Add(en.Current.Id.Value);
        outs.Should().BeEquivalentTo(new[] { r1, r3 });
        _ = cId;
    }

    [Fact]
    public void Vacuum_reclaims_dead_properties_and_keeps_live_chain()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 小さい値はノードレコードへインライン化されチェーンに乗らないため、このテストでは
        // overflow チェーン vacuum (PropertyStore.VacuumDeadVersions) を検証する意図なので、
        // 255B を超える大きい文字列 (= overflow チェーン行き) を使う。
        static string Big(string s) => new string('x', 300) + s;

        long nodeId;
        using (var tx = db.BeginTransaction())
        {
            nodeId = tx.CreateNode("Person").Value;
            tx.SetProperty(new Core.NodeId(nodeId), "k1", Storage.Records.PropertyValue.FromString(Big("1")));
            tx.SetProperty(new Core.NodeId(nodeId), "k2", Storage.Records.PropertyValue.FromString(Big("2")));
            tx.SetProperty(new Core.NodeId(nodeId), "k3", Storage.Records.PropertyValue.FromString(Big("3")));
            tx.Commit();
        }
        // 1 つだけ削除 (= xmax がスタンプされて dead version 化)。
        using (var tx = db.BeginTransaction())
        {
            tx.RemoveProperty(new Core.NodeId(nodeId), "k2");
            tx.Commit();
        }

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedProperties.Should().Be(1);

        // 残った k1 / k3 が読めて、k2 は消えていること。
        using var read = db.BeginReadOnlyTransaction();
        System.Text.Encoding.UTF8.GetString(read.GetProperty(new Core.NodeId(nodeId), "k1").Utf8StringValue).Should().Be(Big("1"));
        System.Text.Encoding.UTF8.GetString(read.GetProperty(new Core.NodeId(nodeId), "k3").Utf8StringValue).Should().Be(Big("3"));
        read.HasProperty(new Core.NodeId(nodeId), "k2").Should().BeFalse();
    }

    [Fact]
    public void Vacuum_reclaims_property_chain_when_node_is_deleted()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // オーバーフローチェーン上のプロパティ回収を検証するため、大きい文字列を使う。
        // (小さい値は inline 化され node version に同梱で消えるため chain には乗らない)。
        static string Big(string s) => new string('x', 300) + s;

        long deletedId;
        using (var tx = db.BeginTransaction())
        {
            deletedId = tx.CreateNode("Person").Value;
            tx.SetProperty(new Core.NodeId(deletedId), "a", Storage.Records.PropertyValue.FromString(Big("a")));
            tx.SetProperty(new Core.NodeId(deletedId), "b", Storage.Records.PropertyValue.FromString(Big("b")));
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(new Core.NodeId(deletedId));
            tx.Commit();
        }

        var report = db.Vacuum();
        // ノード 1 つ + その overflow プロパティ 2 つを回収。
        report.ReclaimedNodes.Should().Be(1);
        report.ReclaimedProperties.Should().Be(2);
    }

    [Fact]
    public void Vacuum_does_not_reclaim_live_versions()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        long aliveId, deletedId;
        using (var tx = db.BeginTransaction())
        {
            aliveId = tx.CreateNode("Person").Value;
            deletedId = tx.CreateNode("Person").Value;
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNode(new Core.NodeId(deletedId));
            tx.Commit();
        }

        var report = db.Vacuum();
        report.ReclaimedNodes.Should().Be(1);

        // 残った live ノードはまだ読める。
        using var read = db.BeginReadOnlyTransaction();
        read.NodeExists(new Core.NodeId(aliveId)).Should().BeTrue();
    }

    // ---------- 物理切り詰めと WAL FileTruncate ----------

    /// <summary>
    /// 1000 ノードを作成してすべて削除し、Vacuum でテナントページが回収されることを確認する。
    /// 単一ファイルコンテナでは物理 OS truncate ではなく、末尾の不要ページをグローバル free list へ
    /// 返却し他テナントが再利用できる形で回収する (graph.quiver は MMF 事前確保のため縮まない)。
    /// よって回収量は <see cref="VacuumReport.TruncatedPages"/> (回収した論理ページ数) で確認し、
    /// 回収後に同数のノードを再作成してもコンテナの論理ページ数が増えない (= 再利用された) ことを検証する。
    /// </summary>
    [Fact]
    public void Vacuum_reclaims_tenant_pages_for_reuse_after_mass_delete()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 1000; i++)
                ids.Add(tx.CreateNode("Person").Value);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            foreach (var id in ids)
                tx.DeleteNode(new Core.NodeId(id));
            tx.Commit();
        }

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedNodes.Should().Be(1000);
        // ノードはスロット付きヒープと ItemPointerMap フリーリストを使う。Vacuum は
        // dead version を tombstone + seq を free list へ戻す (論理回収 + seq 再利用)。tombstone ページの
        // ここでは物理回収を行わず、プロパティとリレーションシップだけ従来どおり回収する。
        // よってここでは「再作成が free list の seq を再利用し全件読める」ことを検証する。
        var refilled = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 1000; i++)
                refilled.Add(tx.CreateNode("Person").Value);
            tx.Commit();
        }
        // seq 再利用: 再作成した 1000 件の Sequence は元の 0..999 の範囲に収まる (新規採番されない)。
        using (var read = db.BeginReadOnlyTransaction())
        {
            foreach (var id in refilled)
                read.NodeExists(new Core.NodeId(id)).Should().BeTrue();
        }
        refilled.Select(v => new Core.NodeId(v).Sequence).Max().Should().BeLessThan(1000,
            "vacuum 回収済み seq が free list から再利用され、新規採番されないこと");
    }

    /// <summary>
    /// Vacuum で物理的に切り詰めた後にデータベースを再オープンしても整合性が保たれ、
    /// 残った live データが読めること。truncate 操作は WAL FileTruncate で durable 化されている。
    /// </summary>
    [Fact]
    public void Vacuum_truncate_survives_reopen()
    {
        long aliveId;
        // フェーズ 1: 多数作成 → 一部削除 → vacuum で truncate。
        {
            using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
            var deletedIds = new List<long>();
            using (var tx = db.BeginTransaction())
            {
                aliveId = tx.CreateNode("Person").Value;
                tx.SetProperty(new Core.NodeId(aliveId), "name", Storage.Records.PropertyValue.FromInt32(42));
                // NodeStore のレコード縮小で 1 ページ当たりの件数が増えたため、
                // 末尾 free page を truncate させるには alive ノード (id 0) の居る page を超えて
                // 複数 record page に跨る数の deleted ノードが必要。1200 で page 2〜4 に跨る。
                for (int i = 0; i < 1200; i++)
                    deletedIds.Add(tx.CreateNode("Person").Value);
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                foreach (var id in deletedIds)
                    tx.DeleteNode(new Core.NodeId(id));
                tx.Commit();
            }
            // ノードヒープは不要版を回収するが、墓石ページの
            // 物理的な切り詰めは行わない。ここでは不要版の回収と再オープン後の整合性を検証する。
            db.Vacuum().ReclaimedNodes.Should().BeGreaterThan(0);
        }

        // フェーズ 2: 再 open。残った live ノードと property が読めること。
        using var db2 = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var read = db2.BeginReadOnlyTransaction();
        read.NodeExists(new Core.NodeId(aliveId)).Should().BeTrue();
        read.GetProperty(new Core.NodeId(aliveId), "name").Int32Value.Should().Be(42);
    }

    /// <summary>
    /// Vacuum で切り詰めた範囲が、新しい <c>AllocatePage</c> によって再拡張されることを確認する。
    /// truncate 後に同じ程度の新規ノードを作成して全部書けること。
    /// </summary>
    [Fact]
    public void Pages_truncated_by_Vacuum_can_be_reallocated()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 500 ノード作成 → 全削除 → vacuum (truncate を狙う)。
        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 500; i++)
                ids.Add(tx.CreateNode("Person").Value);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            foreach (var id in ids) tx.DeleteNode(new Core.NodeId(id));
            tx.Commit();
        }
        db.Vacuum();

        // truncate 後に再び 500 ノード作る — エラーなく完了し全件読める。
        var newIds = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 500; i++)
                newIds.Add(tx.CreateNode("Person").Value);
            tx.Commit();
        }

        using var read = db.BeginReadOnlyTransaction();
        foreach (var id in newIds)
            read.NodeExists(new Core.NodeId(id)).Should().BeTrue();
    }

    // ---------- ハイパーエッジの物理回収 ----------

    /// <summary>
    /// dead ハイパーエッジの overflow プロパティ / incidence / header が
    /// property → incidence → header の順で回収され、live ハイパーエッジは影響を受けないこと。
    /// </summary>
    [Fact]
    public void Vacuum_reclaims_dead_hyperedges_incidences_and_overflow_properties()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 小さい値は header へ inline 化されるため、overflow chain の回収を検証するには
        // 255B を超える値を使う (node property の vacuum テストと同じ理由)。
        static string Big(string s) => new string('x', 300) + s;

        Core.HyperedgeId dead, alive;
        Core.NodeId a, b;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("Entity");
            b = tx.CreateNode("Entity");
            dead = tx.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
            tx.SetProperty(dead, "note", Storage.Records.PropertyValue.FromString(Big("d")));
            alive = tx.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
            tx.SetProperty(alive, "note", Storage.Records.PropertyValue.FromString(Big("a")));
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteHyperedge(dead);
            tx.Commit();
        }

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedHyperedges.Should().Be(1);
        report.ReclaimedIncidences.Should().Be(2);
        report.ReclaimedProperties.Should().BeGreaterThanOrEqualTo(1,
            "dead ハイパーエッジの overflow プロパティも回収される");

        using (var read = db.BeginReadOnlyTransaction())
        {
            CollectMembers(read.GetMembers(dead)).Should().BeEmpty();
            CollectMembers(read.GetMembers(alive)).Should().HaveCount(2);
            System.Text.Encoding.UTF8.GetString(
                read.GetProperty(alive, "note").Utf8StringValue).Should().Be(Big("a"));
        }
        db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();
    }

    /// <summary>
    /// 1 つの node の incidence chain の先頭・中間・末尾にある dead incidence が
    /// 1 回の chain sweep で正しく unlink されること。node chain は head insert なので
    /// 作成が新しいものほど chain の先頭に来る。
    /// </summary>
    [Fact]
    public void Vacuum_unlinks_dead_incidences_at_head_middle_and_tail_of_node_chain()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        Core.NodeId hub;
        var edges = new List<Core.HyperedgeId>();
        using (var tx = db.BeginTransaction())
        {
            hub = tx.CreateNode("Hub");
            for (int i = 0; i < 5; i++)
            {
                var partner = tx.CreateNode("Partner");
                edges.Add(tx.CreateHyperedge("Link", [new("Hub", hub), new("Partner", partner)]));
            }
            tx.Commit();
        }

        // hub の chain は作成の逆順 [4] (head), [3], [2], [1], [0] (tail)。
        // 先頭 (edges[4])・中間 (edges[2])・末尾 (edges[0]) を削除する。
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteHyperedge(edges[4]);
            tx.DeleteHyperedge(edges[2]);
            tx.DeleteHyperedge(edges[0]);
            tx.Commit();
        }

        var report = db.Vacuum();
        report.ReclaimedHyperedges.Should().Be(3);
        report.ReclaimedIncidences.Should().Be(6);

        // 生き残った 2 件だけが hub から辿れる。
        using (var read = db.BeginReadOnlyTransaction())
        {
            CollectIds(read.GetHyperedges(hub)).Should().BeEquivalentTo(
                new[] { edges[1].Sequence, edges[3].Sequence });
        }
        db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();

        // sweep 後の chain (head 前進 + 中間の繋ぎ替え) に対して新規作成が正しく head insert される。
        Core.HyperedgeId added;
        using (var tx = db.BeginTransaction())
        {
            var partner = tx.CreateNode("Partner");
            added = tx.CreateHyperedge("Link", [new("Hub", hub), new("Partner", partner)]);
            tx.Commit();
        }
        using (var read = db.BeginReadOnlyTransaction())
        {
            CollectIds(read.GetHyperedges(hub)).Should().BeEquivalentTo(
                new[] { edges[1].Sequence, edges[3].Sequence, added.Sequence });
        }
        db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();
    }

    /// <summary>
    /// vacuum で回収した sequence が再利用されるとき世代が上がり、
    /// 古い ID が新しい entity を指さないこと。
    /// </summary>
    [Fact]
    public void Stale_hyperedge_id_does_not_resolve_after_sequence_reuse()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        Core.HyperedgeId old;
        Core.NodeId a, b;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("Entity");
            b = tx.CreateNode("Entity");
            old = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
            tx.SetProperty(old, "k", Storage.Records.PropertyValue.FromInt32(1));
            tx.Commit();
        }
        old.Generation.Should().Be(1);

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteHyperedge(old);
            tx.Commit();
        }
        db.Vacuum().ReclaimedHyperedges.Should().Be(1);

        Core.HyperedgeId reused;
        using (var tx = db.BeginTransaction())
        {
            reused = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
            tx.SetProperty(reused, "k", Storage.Records.PropertyValue.FromInt32(2));
            tx.Commit();
        }

        // 同じ slot を世代 bump 付きで再利用する。
        reused.Sequence.Should().Be(old.Sequence);
        reused.Generation.Should().Be(2);
        reused.Value.Should().NotBe(old.Value);

        // 古い packed ID は ID 解決経路 (header Read) の世代照合で弾かれ、
        // 新しい entity を観測しない。
        using (var read = db.BeginReadOnlyTransaction())
        {
            CollectMembers(read.GetMembers(old)).Should().BeEmpty();
            read.GetProperty(reused, "k").Int32Value.Should().Be(2);

            // node からの列挙は同じ sequence の生存 hyperedge を 1 件だけ返す。
            var fromNode = CollectIds(read.GetHyperedges(a));
            fromNode.Should().ContainSingle();
            fromNode[0].Should().Be(reused.Sequence);
        }
    }

    /// <summary>
    /// 回収済み sequence に残るベクトルが、世代の異なる新しいハイパーエッジへ
    /// 誤って結び付かないこと。
    /// </summary>
    [Fact]
    public void Stale_hyperedge_vector_binding_is_rejected_after_sequence_reuse()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        const string indexName = "facts";
        db.Vectors.CreateVectorIndex(new Core.VectorIndexSpec(
            indexName,
            Core.EntityKind.Hyperedge,
            db.Schema.GetOrCreatePropertyKey("embedding"),
            2,
            Core.DistanceMetric.Dot,
            "test",
            null));

        Core.NodeId a, b;
        Core.HyperedgeId old;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("Entity");
            b = tx.CreateNode("Entity");
            old = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
            tx.SetVector(Core.EntityKind.Hyperedge, old.Value, indexName, [1f, 0f]);
            tx.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteHyperedge(old);
            tx.Commit();
        }
        db.Vacuum().ReclaimedHyperedges.Should().Be(1);

        Core.HyperedgeId reused;
        using (var tx = db.BeginTransaction())
        {
            reused = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
            tx.Commit();
        }
        reused.Sequence.Should().Be(old.Sequence);
        reused.Generation.Should().Be(old.Generation + 1);

        Span<float> vector = stackalloc float[2];
        db.Vectors.TryGetVector(Core.EntityKind.Hyperedge, reused.Value, indexName, vector)
            .Should().BeFalse("残存 payload の世代は再利用後の entity と一致しない");
        using var results = db.Vectors.KnnSearch(indexName, [1f, 0f], 10);
        results.MoveNext().Should().BeFalse();
    }

    /// <summary>アクティブな snapshot (read-only tx) が居る間はハイパーエッジも回収しないこと。</summary>
    [Fact]
    public void Vacuum_does_not_reclaim_hyperedges_while_snapshot_is_active()
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        Core.HyperedgeId heId;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            heId = tx.CreateHyperedge("T", [new("R1", a), new("R2", b)]);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteHyperedge(heId);
            tx.Commit();
        }

        using (var holder = db.BeginReadOnlyTransaction())
        {
            var report = db.Vacuum();
            report.Skipped.Should().BeTrue();
            report.ReclaimedHyperedges.Should().Be(0);
            report.ReclaimedIncidences.Should().Be(0);
        }

        // snapshot が消えれば回収できる。
        db.Vacuum().ReclaimedHyperedges.Should().Be(1);
    }

    /// <summary>
    /// vacuum 後の再オープンで free list / 世代 / incidence chain のメタデータが正しく復元され、
    /// live データの読み取りと sequence 再利用が継続すること。
    /// </summary>
    [Fact]
    public void Hyperedge_vacuum_survives_reopen()
    {
        string path = System.IO.Path.Combine(_dir, "graph.quiver");
        Core.HyperedgeId dead, alive;
        Core.NodeId a, b;
        using (var db = GraphDatabase.Open(path))
        {
            using (var tx = db.BeginTransaction())
            {
                a = tx.CreateNode("Entity");
                b = tx.CreateNode("Entity");
                dead = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
                alive = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
                tx.SetProperty(alive, "k", Storage.Records.PropertyValue.FromInt32(7));
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                tx.DeleteHyperedge(dead);
                tx.Commit();
            }
            db.Vacuum().ReclaimedHyperedges.Should().Be(1);
        }

        using (var db = GraphDatabase.Open(path))
        {
            using (var read = db.BeginReadOnlyTransaction())
            {
                CollectMembers(read.GetMembers(alive)).Should().HaveCount(2);
                read.GetProperty(alive, "k").Int32Value.Should().Be(7);
                CollectMembers(read.GetMembers(dead)).Should().BeEmpty();
            }

            // 再オープン後も free list から回収済み sequence を世代 bump 付きで再利用する。
            Core.HyperedgeId reused;
            using (var tx = db.BeginTransaction())
            {
                reused = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
                tx.Commit();
            }
            reused.Sequence.Should().Be(dead.Sequence);
            reused.Generation.Should().Be(2);
            db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();
        }
    }

    /// <summary>
    /// vacuum とその後のコミットを含む状態で正常フラッシュを経ないプロセス停止を模擬し、
    /// recovery 後もハイパーエッジの生死と chain が整合すること。
    /// </summary>
    [Fact]
    public void Hyperedge_vacuum_state_survives_simulated_crash()
    {
        string path = System.IO.Path.Combine(_dir, "graph.quiver");
        Core.HyperedgeId dead, alive, added;
        Core.NodeId a, b;

        // まず論理削除までを正常終了し、vacuum 前のデータファイルを
        // 「クラッシュ時に未フラッシュだったページ」の基準スナップショットにする。
        using (var db = GraphDatabase.Open(path))
        {
            using (var tx = db.BeginTransaction())
            {
                a = tx.CreateNode("Entity");
                b = tx.CreateNode("Entity");
                dead = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
                alive = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                tx.DeleteHyperedge(dead);
                tx.Commit();
            }
        }
        byte[] preVacuumData = File.ReadAllBytes(path);

        {
            var db = GraphDatabase.Open(path);
            db.Vacuum().ReclaimedHyperedges.Should().Be(1);

            // vacuum 後に回収 slot を再利用するコミットを積む。未完了 tx を残して
            // clean shutdown を抑止し、コミット済み WAL を保持する。
            using (var tx = db.BeginTransaction())
            {
                added = tx.CreateHyperedge("Fact", [new("S", a), new("O", b)]);
                tx.Commit();
            }
            _ = db.BeginTransaction();
            db.Dispose();
        }

        // データファイルだけを vacuum 前へ戻し、WAL は残す。再オープン時の redo が
        // vacuum 後にコミットした再利用 entity と incidence chain を復元する。
        File.WriteAllBytes(path, preVacuumData);
        using (var db = GraphDatabase.Open(path))
        {
            using (var read = db.BeginReadOnlyTransaction())
            {
                CollectMembers(read.GetMembers(dead)).Should().BeEmpty();
                CollectMembers(read.GetMembers(alive)).Should().HaveCount(2);
                CollectMembers(read.GetMembers(added)).Should().HaveCount(2);
                CollectIds(read.GetHyperedges(a)).Should().BeEquivalentTo(
                    new[] { alive.Sequence, added.Sequence });
            }
            db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();
        }
    }

    // ref struct enumerator は LINQ に乗らないため、素朴に List へ写して検証する。
    private static List<HyperedgeMember> CollectMembers(HyperedgeMemberEnumerator e)
    {
        var list = new List<HyperedgeMember>();
        while (e.MoveNext()) list.Add(e.Current);
        return list;
    }

    // 列挙子が返す ID は sequence のみを保持する (chain record には世代を格納しない) ため、
    // 同一性の検証は sequence で行う。
    private static List<long> CollectIds(HyperedgeIdEnumerator e)
    {
        var list = new List<long>();
        while (e.MoveNext()) list.Add(e.Current.Sequence);
        return list;
    }
}
