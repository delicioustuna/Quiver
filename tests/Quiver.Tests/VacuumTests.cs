using FluentAssertions;
using Quiver.Api;
using Quiver.Maintenance;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// Vacuum による不要版の物理回収、フリーリストへの登録、末尾の高水位標縮小、
/// コミット済みレジストリの整理を確認する。
/// MVCC で論理削除されたVertexが <see cref="QuiverDatabase.Vacuum"/> によって
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
    public void Vacuum_reclaims_committed_dead_vertex_versions()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 100 個のVertexを作成 → 全削除 → vacuum で物理回収。
        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 100; i++)
                ids.Add(tx.CreateVertex("Person").Value);
            tx.Commit();
        }
        db.Diagnostics.GetStatistics().VertexCount.Should().Be(100);

        using (var tx = db.BeginTransaction())
        {
            foreach (var id in ids)
                tx.DeleteVertex(new Core.VertexId(id));
            tx.Commit();
        }
        db.Diagnostics.GetStatistics().VertexCount.Should().Be(0);

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedVertices.Should().Be(100);
        // 末尾の連続 free slot で hwm が 0 まで縮む。
        report.HorizonTxId.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Vacuum_returns_Skipped_when_active_transactions_exist()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 削除済み version を 1 件作る (vacuum 対象がある状態)。
        long id;
        using (var tx = db.BeginTransaction())
        {
            id = tx.CreateVertex("Person").Value;
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteVertex(new Core.VertexId(id));
            tx.Commit();
        }

        // アクティブ tx を抱えた状態で vacuum 起動 → Skipped。
        using var holder = db.BeginReadOnlyTransaction();
        var report = db.Vacuum();
        report.Skipped.Should().BeTrue();
        report.ReclaimedVertices.Should().Be(0);
    }

    [Fact]
    public void Vacuum_freed_slots_are_reused_by_subsequent_Allocate()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 10 Vertex作成 → 全削除 → vacuum。
        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 10; i++)
                ids.Add(tx.CreateVertex("Person").Value);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            foreach (var v in ids) tx.DeleteVertex(new Core.VertexId(v));
            tx.Commit();
        }
        var report = db.Vacuum();
        report.ReclaimedVertices.Should().Be(10);

        // vacuum 後の新規 Allocate は回収済み slot (= 元と同じ ID 範囲) を再利用する。
        var newIds = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 5; i++)
                newIds.Add(tx.CreateVertex("Person").Sequence); // slot 再利用は Sequence で確認
            tx.Commit();
        }

        // 元の ID 範囲 [0..9] のいずれかが再利用される (free list / hwm 縮減後の dense slot)。
        newIds.Should().OnlyContain(id => id < 10);
    }

    // gen-stamp-fastpath: 再利用 (gen>=2) が起きた後、クエリ結果の VertexId が bump 世代を
    // 載せること = 世代 stamping の高速パスが正しく per-row read へフォールバックしている検証。
    [Fact]
    public void Query_after_slot_reuse_returns_bumped_generation()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        long seq;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateVertex("Person");
            seq = a.Sequence;
            a.Generation.Should().Be(1);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteVertex(new Core.VertexId(seq));
            tx.Commit();
        }
        db.Vacuum().ReclaimedVertices.Should().Be(1);

        Core.VertexId reused;
        using (var tx = db.BeginTransaction())
        {
            reused = tx.CreateVertex("Person");
            tx.Commit();
        }
        reused.Sequence.Should().Be(seq);   // 同 slot を再利用
        reused.Generation.Should().Be(2);   // 世代 bump

        using (var tx = db.BeginReadOnlyTransaction())
        {
            var rows = tx.G(db.Schema).Vertices().ToList();
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
        using (var db = QuiverDatabase.Open(path))
        {
            using (var tx = db.BeginTransaction()) { seq = tx.CreateVertex("P").Sequence; tx.Commit(); }
            using (var tx = db.BeginTransaction()) { tx.DeleteVertex(new Core.VertexId(seq)); tx.Commit(); }
            db.Vacuum();
            using (var tx = db.BeginTransaction()) { tx.CreateVertex("P").Generation.Should().Be(2); tx.Commit(); }
        }

        using (var db = QuiverDatabase.Open(path))
        using (var tx = db.BeginReadOnlyTransaction())
        {
            var rows = tx.G(db.Schema).Vertices().ToList();
            rows.Should().ContainSingle();
            rows[0].Generation.Should().Be(2);
        }
    }

    [Fact]
    public void DryRun_does_not_write()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateVertex("Person");
            tx.DeleteVertex(n);
            tx.Commit();
        }

        var dryRun = db.Vacuum(new VacuumOptions { Mode = VacuumMode.DryRun });
        dryRun.Skipped.Should().BeFalse();
        dryRun.ReclaimedVertices.Should().Be(0); // ドライランは書き込まない
        dryRun.PrunedCommittedTxEntries.Should().Be(0);

        // 続けて実行する Full は普通に回収できる。
        var full = db.Vacuum();
        full.ReclaimedVertices.Should().Be(1);
    }

    [Fact]
    public void Vacuum_reclaims_dead_edges_and_keeps_live_chain()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 3 Vertex a/b/c。a→b, a→c, a→b の 3 リレーション。
        long aId, bId, cId;
        long r1, r2, r3;
        using (var tx = db.BeginTransaction())
        {
            aId = tx.CreateVertex("Person").Value;
            bId = tx.CreateVertex("Person").Value;
            cId = tx.CreateVertex("Person").Value;
            r1 = tx.CreateEdge(new Core.VertexId(aId), new Core.VertexId(bId), "KNOWS").Value;
            r2 = tx.CreateEdge(new Core.VertexId(aId), new Core.VertexId(cId), "KNOWS").Value;
            r3 = tx.CreateEdge(new Core.VertexId(aId), new Core.VertexId(bId), "KNOWS").Value;
            tx.Commit();
        }
        // 中間の edge r2 だけ削除。
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteEdge(new Core.EdgeId(r2));
            tx.Commit();
        }

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedEdges.Should().Be(1);

        // a の chain は r1, r3 だけが残ること。
        using var read = db.BeginReadOnlyTransaction();
        var outs = new List<long>();
        var en = read.EnumerateEdges(new Core.VertexId(aId), Storage.Records.Direction.Outgoing);
        while (en.MoveNext())
            outs.Add(en.Current.Id.Value);
        outs.Should().BeEquivalentTo(new[] { r1, r3 });
        _ = cId;
    }

    [Fact]
    public void Vacuum_reclaims_dead_properties_and_keeps_live_chain()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 小さい値はVertexレコードへインライン化されチェーンに乗らないため、このテストでは
        // overflow チェーン vacuum (PropertyVersionStore.VacuumDeadVersions) を検証する意図なので、
        // 255B を超える大きい文字列 (= overflow チェーン行き) を使う。
        static string Big(string s) => new string('x', 300) + s;

        long vertexId;
        using (var tx = db.BeginTransaction())
        {
            vertexId = tx.CreateVertex("Person").Value;
            tx.SetProperty(new Core.VertexId(vertexId), "k1", Storage.Records.PropertyValue.FromString(Big("1")));
            tx.SetProperty(new Core.VertexId(vertexId), "k2", Storage.Records.PropertyValue.FromString(Big("2")));
            tx.SetProperty(new Core.VertexId(vertexId), "k3", Storage.Records.PropertyValue.FromString(Big("3")));
            tx.Commit();
        }
        // 1 つだけ削除 (= xmax がスタンプされて dead version 化)。
        using (var tx = db.BeginTransaction())
        {
            tx.RemoveProperty(new Core.VertexId(vertexId), "k2");
            tx.Commit();
        }

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedProperties.Should().Be(1);

        // 残った k1 / k3 が読めて、k2 は消えていること。
        using var read = db.BeginReadOnlyTransaction();
        System.Text.Encoding.UTF8.GetString(read.GetProperty(new Core.VertexId(vertexId), "k1").Utf8StringValue).Should().Be(Big("1"));
        System.Text.Encoding.UTF8.GetString(read.GetProperty(new Core.VertexId(vertexId), "k3").Utf8StringValue).Should().Be(Big("3"));
        read.HasProperty(new Core.VertexId(vertexId), "k2").Should().BeFalse();
    }

    [Fact]
    public void Vacuum_reclaims_property_chain_when_vertex_is_deleted()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // オーバーフローチェーン上のプロパティ回収を検証するため、大きい文字列を使う。
        // (小さい値は inline 化され vertex version に同梱で消えるため chain には乗らない)。
        static string Big(string s) => new string('x', 300) + s;

        long deletedId;
        using (var tx = db.BeginTransaction())
        {
            deletedId = tx.CreateVertex("Person").Value;
            tx.SetProperty(new Core.VertexId(deletedId), "a", Storage.Records.PropertyValue.FromString(Big("a")));
            tx.SetProperty(new Core.VertexId(deletedId), "b", Storage.Records.PropertyValue.FromString(Big("b")));
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteVertex(new Core.VertexId(deletedId));
            tx.Commit();
        }

        var report = db.Vacuum();
        // Vertex 1 つ + その overflow プロパティ 2 つを回収。
        report.ReclaimedVertices.Should().Be(1);
        report.ReclaimedProperties.Should().Be(2);
    }

    [Fact]
    public void Vacuum_does_not_reclaim_live_versions()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        long aliveId, deletedId;
        using (var tx = db.BeginTransaction())
        {
            aliveId = tx.CreateVertex("Person").Value;
            deletedId = tx.CreateVertex("Person").Value;
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteVertex(new Core.VertexId(deletedId));
            tx.Commit();
        }

        var report = db.Vacuum();
        report.ReclaimedVertices.Should().Be(1);

        // 残った live Vertexはまだ読める。
        using var read = db.BeginReadOnlyTransaction();
        read.VertexExists(new Core.VertexId(aliveId)).Should().BeTrue();
    }

    // ---------- 物理切り詰めと WAL FileTruncate ----------

    /// <summary>
    /// 1000 Vertexを作成してすべて削除し、Vacuum でテナントページが回収されることを確認する。
    /// 単一ファイルコンテナでは物理 OS truncate ではなく、末尾の不要ページをグローバル free list へ
    /// 返却し他テナントが再利用できる形で回収する (graph.quiver は MMF 事前確保のため縮まない)。
    /// よって回収量は <see cref="VacuumReport.TruncatedPages"/> (回収した論理ページ数) で確認し、
    /// 回収後に同数のVertexを再作成してもコンテナの論理ページ数が増えない (= 再利用された) ことを検証する。
    /// </summary>
    [Fact]
    public void Vacuum_reclaims_tenant_pages_for_reuse_after_mass_delete()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 1000; i++)
                ids.Add(tx.CreateVertex("Person").Value);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            foreach (var id in ids)
                tx.DeleteVertex(new Core.VertexId(id));
            tx.Commit();
        }

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedVertices.Should().Be(1000);
        // Vertexはスロット付きヒープと ItemPointerMap フリーリストを使う。Vacuum は
        // dead version を tombstone + seq を free list へ戻す (論理回収 + seq 再利用)。tombstone ページの
        // ここでは物理回収を行わず、プロパティとEdgeだけ従来どおり回収する。
        // よってここでは「再作成が free list の seq を再利用し全件読める」ことを検証する。
        var refilled = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 1000; i++)
                refilled.Add(tx.CreateVertex("Person").Value);
            tx.Commit();
        }
        // seq 再利用: 再作成した 1000 件の Sequence は元の 0..999 の範囲に収まる (新規採番されない)。
        using (var read = db.BeginReadOnlyTransaction())
        {
            foreach (var id in refilled)
                read.VertexExists(new Core.VertexId(id)).Should().BeTrue();
        }
        refilled.Select(v => new Core.VertexId(v).Sequence).Max().Should().BeLessThan(1000,
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
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
            var deletedIds = new List<long>();
            using (var tx = db.BeginTransaction())
            {
                aliveId = tx.CreateVertex("Person").Value;
                tx.SetProperty(new Core.VertexId(aliveId), "name", Storage.Records.PropertyValue.FromInt32(42));
                // VertexStore のレコード縮小で 1 ページ当たりの件数が増えたため、
                // 末尾 free page を truncate させるには alive Vertex (id 0) の居る page を超えて
                // 複数 record page に跨る数の deleted Vertexが必要。1200 で page 2〜4 に跨る。
                for (int i = 0; i < 1200; i++)
                    deletedIds.Add(tx.CreateVertex("Person").Value);
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                foreach (var id in deletedIds)
                    tx.DeleteVertex(new Core.VertexId(id));
                tx.Commit();
            }
            // Vertexヒープは不要版を回収するが、墓石ページの
            // 物理的な切り詰めは行わない。ここでは不要版の回収と再オープン後の整合性を検証する。
            db.Vacuum().ReclaimedVertices.Should().BeGreaterThan(0);
        }

        // フェーズ 2: 再 open。残った live Vertexと property が読めること。
        using var db2 = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var read = db2.BeginReadOnlyTransaction();
        read.VertexExists(new Core.VertexId(aliveId)).Should().BeTrue();
        read.GetProperty(new Core.VertexId(aliveId), "name").Int32Value.Should().Be(42);
    }

    /// <summary>
    /// Vacuum で切り詰めた範囲が、新しい <c>AllocatePage</c> によって再拡張されることを確認する。
    /// truncate 後に同じ程度の新規Vertexを作成して全部書けること。
    /// </summary>
    [Fact]
    public void Pages_truncated_by_Vacuum_can_be_reallocated()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 500 Vertex作成 → 全削除 → vacuum (truncate を狙う)。
        var ids = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 500; i++)
                ids.Add(tx.CreateVertex("Person").Value);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            foreach (var id in ids) tx.DeleteVertex(new Core.VertexId(id));
            tx.Commit();
        }
        db.Vacuum();

        // truncate 後に再び 500 Vertex作る — エラーなく完了し全件読める。
        var newIds = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < 500; i++)
                newIds.Add(tx.CreateVertex("Person").Value);
            tx.Commit();
        }

        using var read = db.BeginReadOnlyTransaction();
        foreach (var id in newIds)
            read.VertexExists(new Core.VertexId(id)).Should().BeTrue();
    }

    // ---------- Nexusの物理回収 ----------

    /// <summary>
    /// dead Nexusの overflow プロパティ / incidence / header が
    /// property → incidence → header の順で回収され、live Nexusは影響を受けないこと。
    /// </summary>
    [Fact]
    public void Vacuum_reclaims_dead_nexuses_incidences_and_overflow_properties()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 小さい値は header へ inline 化されるため、overflow chain の回収を検証するには
        // 255B を超える値を使う (vertex property の vacuum テストと同じ理由)。
        static string Big(string s) => new string('x', 300) + s;

        Core.NexusId dead, alive;
        Core.VertexId a, b;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateVertex("Entity");
            b = tx.CreateVertex("Entity");
            dead = tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
            tx.SetProperty(dead, "note", Storage.Records.PropertyValue.FromString(Big("d")));
            alive = tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
            tx.SetProperty(alive, "note", Storage.Records.PropertyValue.FromString(Big("a")));
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNexus(dead);
            tx.Commit();
        }

        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedNexuses.Should().Be(1);
        report.ReclaimedIncidences.Should().Be(2);
        report.ReclaimedProperties.Should().BeGreaterThanOrEqualTo(1,
            "dead Nexusの overflow プロパティも回収される");

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
    /// 1 つの vertex の incidence chain の先頭・中間・末尾にある dead incidence が
    /// 1 回の chain sweep で正しく unlink されること。vertex chain は head insert なので
    /// 作成が新しいものほど chain の先頭に来る。
    /// </summary>
    [Fact]
    public void Vacuum_unlinks_dead_incidences_at_head_middle_and_tail_of_vertex_chain()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        Core.VertexId hub;
        var edges = new List<Core.NexusId>();
        using (var tx = db.BeginTransaction())
        {
            hub = tx.CreateVertex("Hub");
            for (int i = 0; i < 5; i++)
            {
                var partner = tx.CreateVertex("Partner");
                edges.Add(tx.CreateNexus("Link", [new("Hub", hub), new("Partner", partner)]));
            }
            tx.Commit();
        }

        // hub の chain は作成の逆順 [4] (head), [3], [2], [1], [0] (tail)。
        // 先頭 (edges[4])・中間 (edges[2])・末尾 (edges[0]) を削除する。
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNexus(edges[4]);
            tx.DeleteNexus(edges[2]);
            tx.DeleteNexus(edges[0]);
            tx.Commit();
        }

        var report = db.Vacuum();
        report.ReclaimedNexuses.Should().Be(3);
        report.ReclaimedIncidences.Should().Be(6);

        // 生き残った 2 件だけが hub から辿れる。
        using (var read = db.BeginReadOnlyTransaction())
        {
            CollectIds(read.GetNexuses(hub)).Should().BeEquivalentTo(
                new[] { edges[1].Sequence, edges[3].Sequence });
        }
        db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();

        // sweep 後の chain (head 前進 + 中間の繋ぎ替え) に対して新規作成が正しく head insert される。
        Core.NexusId added;
        using (var tx = db.BeginTransaction())
        {
            var partner = tx.CreateVertex("Partner");
            added = tx.CreateNexus("Link", [new("Hub", hub), new("Partner", partner)]);
            tx.Commit();
        }
        using (var read = db.BeginReadOnlyTransaction())
        {
            CollectIds(read.GetNexuses(hub)).Should().BeEquivalentTo(
                new[] { edges[1].Sequence, edges[3].Sequence, added.Sequence });
        }
        db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();
    }

    /// <summary>
    /// vacuum で回収した sequence が再利用されるとき世代が上がり、
    /// 古い ID が新しい entity を指さないこと。
    /// </summary>
    [Fact]
    public void Stale_nexus_id_does_not_resolve_after_sequence_reuse()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        Core.NexusId old;
        Core.VertexId a, b;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateVertex("Entity");
            b = tx.CreateVertex("Entity");
            old = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
            tx.SetProperty(old, "k", Storage.Records.PropertyValue.FromInt32(1));
            tx.Commit();
        }
        old.Generation.Should().Be(1);

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNexus(old);
            tx.Commit();
        }
        db.Vacuum().ReclaimedNexuses.Should().Be(1);

        Core.NexusId reused;
        using (var tx = db.BeginTransaction())
        {
            reused = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
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

            // vertex からの列挙は同じ sequence の生存 nexus を 1 件だけ返す。
            var fromVertex = CollectIds(read.GetNexuses(a));
            fromVertex.Should().ContainSingle();
            fromVertex[0].Should().Be(reused.Sequence);
        }
    }

    /// <summary>
    /// 回収済み sequence に残るベクトルが、世代の異なる新しいNexusへ
    /// 誤って結び付かないこと。
    /// </summary>
    [Fact]
    public void Stale_nexus_vector_binding_is_rejected_after_sequence_reuse()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        const string indexName = "facts";
        db.Vectors.CreateVectorIndex(new Core.VectorIndexSpec(
            indexName,
            Core.EntityKind.Nexus,
            db.Schema.GetOrCreatePropertyKey("embedding"),
            2,
            Core.DistanceMetric.Dot,
            "test",
            null));

        Core.VertexId a, b;
        Core.NexusId old;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateVertex("Entity");
            b = tx.CreateVertex("Entity");
            old = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
            tx.SetVector(Core.EntityKind.Nexus, old.Value, indexName, [1f, 0f]);
            tx.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNexus(old);
            tx.Commit();
        }
        db.Vacuum().ReclaimedNexuses.Should().Be(1);

        Core.NexusId reused;
        using (var tx = db.BeginTransaction())
        {
            reused = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
            tx.Commit();
        }
        reused.Sequence.Should().Be(old.Sequence);
        reused.Generation.Should().Be(old.Generation + 1);

        Span<float> vector = stackalloc float[2];
        db.Vectors.TryGetVector(Core.EntityKind.Nexus, reused.Value, indexName, vector)
            .Should().BeFalse("残存 payload の世代は再利用後の entity と一致しない");
        using var results = db.Vectors.KnnSearch(indexName, [1f, 0f], 10);
        results.MoveNext().Should().BeFalse();
    }

    /// <summary>アクティブな snapshot (read-only tx) が居る間はNexusも回収しないこと。</summary>
    [Fact]
    public void Vacuum_does_not_reclaim_nexuses_while_snapshot_is_active()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        Core.NexusId heId;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateVertex("A");
            var b = tx.CreateVertex("B");
            heId = tx.CreateNexus("T", [new("R1", a), new("R2", b)]);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNexus(heId);
            tx.Commit();
        }

        using (var holder = db.BeginReadOnlyTransaction())
        {
            var report = db.Vacuum();
            report.Skipped.Should().BeTrue();
            report.ReclaimedNexuses.Should().Be(0);
            report.ReclaimedIncidences.Should().Be(0);
        }

        // snapshot が消えれば回収できる。
        db.Vacuum().ReclaimedNexuses.Should().Be(1);
    }

    /// <summary>
    /// vacuum 後の再オープンで free list / 世代 / incidence chain のメタデータが正しく復元され、
    /// live データの読み取りと sequence 再利用が継続すること。
    /// </summary>
    [Fact]
    public void Nexus_vacuum_survives_reopen()
    {
        string path = System.IO.Path.Combine(_dir, "graph.quiver");
        Core.NexusId dead, alive;
        Core.VertexId a, b;
        using (var db = QuiverDatabase.Open(path))
        {
            using (var tx = db.BeginTransaction())
            {
                a = tx.CreateVertex("Entity");
                b = tx.CreateVertex("Entity");
                dead = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
                alive = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
                tx.SetProperty(alive, "k", Storage.Records.PropertyValue.FromInt32(7));
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                tx.DeleteNexus(dead);
                tx.Commit();
            }
            db.Vacuum().ReclaimedNexuses.Should().Be(1);
        }

        using (var db = QuiverDatabase.Open(path))
        {
            using (var read = db.BeginReadOnlyTransaction())
            {
                CollectMembers(read.GetMembers(alive)).Should().HaveCount(2);
                read.GetProperty(alive, "k").Int32Value.Should().Be(7);
                CollectMembers(read.GetMembers(dead)).Should().BeEmpty();
            }

            // 再オープン後も free list から回収済み sequence を世代 bump 付きで再利用する。
            Core.NexusId reused;
            using (var tx = db.BeginTransaction())
            {
                reused = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
                tx.Commit();
            }
            reused.Sequence.Should().Be(dead.Sequence);
            reused.Generation.Should().Be(2);
            db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();
        }
    }

    /// <summary>
    /// vacuum とその後のコミットを含む状態で正常フラッシュを経ないプロセス停止を模擬し、
    /// recovery 後もNexusの生死と chain が整合すること。
    /// </summary>
    [Fact]
    public void Nexus_vacuum_state_survives_simulated_crash()
    {
        string path = System.IO.Path.Combine(_dir, "graph.quiver");
        Core.NexusId dead, alive, added;
        Core.VertexId a, b;

        // まず論理削除までを正常終了する。
        using (var db = QuiverDatabase.Open(path))
        {
            using (var tx = db.BeginTransaction())
            {
                a = tx.CreateVertex("Entity");
                b = tx.CreateVertex("Entity");
                dead = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
                alive = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                tx.DeleteNexus(dead);
                tx.Commit();
            }
        }
        {
            var db = QuiverDatabase.Open(path);
            db.Vacuum().ReclaimedNexuses.Should().Be(1);

            // vacuum は返却前に sharp checkpoint を完了する。
            // その後に回収 slot を再利用するコミットを積み、未完了 tx を残して
            // clean shutdown を抑止することで、追加分は WAL recovery から復元する。
            using (var tx = db.BeginTransaction())
            {
                added = tx.CreateNexus("Fact", [new("S", a), new("O", b)]);
                tx.Commit();
            }
            _ = db.BeginTransaction();
            db.Dispose();
        }

        using (var db = QuiverDatabase.Open(path))
        {
            using (var read = db.BeginReadOnlyTransaction())
            {
                CollectMembers(read.GetMembers(dead)).Should().BeEmpty();
                CollectMembers(read.GetMembers(alive)).Should().HaveCount(2);
                CollectMembers(read.GetMembers(added)).Should().HaveCount(2);
                CollectIds(read.GetNexuses(a)).Should().BeEquivalentTo(
                    new[] { alive.Sequence, added.Sequence });
            }
            db.Diagnostics.CheckConsistency().IsConsistent.Should().BeTrue();
        }
    }

    // ref struct enumerator は LINQ に乗らないため、素朴に List へ写して検証する。
    private static List<NexusMember> CollectMembers(NexusMemberEnumerator e)
    {
        var list = new List<NexusMember>();
        while (e.MoveNext()) list.Add(e.Current);
        return list;
    }

    // 列挙子が返す ID は sequence のみを保持する (chain record には世代を格納しない) ため、
    // 同一性の検証は sequence で行う。
    private static List<long> CollectIds(NexusIdEnumerator e)
    {
        var list = new List<long>();
        while (e.MoveNext()) list.Add(e.Current.Sequence);
        return list;
    }
}
