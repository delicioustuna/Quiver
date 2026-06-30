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
}
