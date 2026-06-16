using System.Buffers.Binary;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FTS-6 durability contract for the full-text lane, at the engine level
/// (<see cref="GraphDatabase"/> / <c>g.Search</c>). Mirrors the BA-9
/// crash-contract style (commit -> simulated kill -> reopen -> recovery) but for
/// the inverted index: after a kill the live search result must equal the
/// committed document set — committed postings survive, uncommitted ones are
/// rolled back. Because postings / norms are ordinary B+Trees (design 13 §3/§4),
/// recovery reuses the existing ARIES page-WAL machinery (FT-17/18/19).
///
/// Kill model: as in BA-9's KillProcessSimulator, on Windows a true process kill
/// can't reopen its own exclusive file handle, so a kill is approximated by
/// dropping the handle (Dispose) + forcing finalizers, then reopening the file.
/// Anything that returned from <see cref="IGraphTransaction.Commit"/> must be
/// recovered; anything uncommitted must not resurface.
/// </summary>
public sealed class FullTextCrashContractTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly string _path;

    public FullTextCrashContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts6_crash_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 5 && Directory.Exists(_dir); attempt++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (IOException) { GcSettle(); Thread.Sleep(50 * (attempt + 1)); }
            catch (UnauthorizedAccessException) { GcSettle(); Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private GraphDatabase Open() => GraphDatabase.Open(_path);

    /// <summary>
    /// Open a fresh database and create the full-text index. Only used for the very
    /// first open in a scenario; reopen uses <see cref="Open"/> which re-materializes
    /// the index from the catalog (FTS-2).
    /// </summary>
    private GraphDatabase OpenAndCreateIndex()
    {
        var db = Open();
        db.Schema.CreateFullTextIndex(Index, "Doc", "body");
        return db;
    }

    /// <summary>Simulated process kill: drop the handle without a graceful, fully-flushed shutdown path and force finalizers so the file can be reopened.</summary>
    private static void Kill(GraphDatabase db)
    {
        try { db.Dispose(); } catch { /* the lost in-flight close is the point of a kill */ }
        GcSettle();
    }

    private static void GcSettle()
    {
        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static NodeId Ingest(GraphDatabase db, string body)
    {
        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString(body));
        tx.Commit();
        return n;
    }

    private static List<NodeId> Search(GraphDatabase db, string query, int k = 10)
    {
        using var rtx = db.BeginReadOnlyTransaction();
        return rtx.G(db.Schema).Search(Index, query, k).ToList();
    }

    // ===== (a) committed full-text docs survive a kill and stay searchable =====

    [Fact]
    public void Committed_docs_survive_kill_and_remain_searchable()
    {
        NodeId alpha, beta, gamma;
        var db = OpenAndCreateIndex();
        alpha = Ingest(db, "the quick alpha fox marker0001");
        beta = Ingest(db, "a slow beta dog marker0002");
        gamma = Ingest(db, "lazy gamma cat marker0003");

        Kill(db);

        using var reopened = Open();
        // The index is re-materialized from the catalog; every committed doc is
        // findable by its unique marker term, and the shared term returns all three.
        Search(reopened, "marker0001").Should().ContainSingle().Which.Should().Be(alpha);
        Search(reopened, "marker0002").Should().ContainSingle().Which.Should().Be(beta);
        Search(reopened, "marker0003").Should().ContainSingle().Which.Should().Be(gamma);
    }

    // ===== (b) uncommitted ingest leaves no postings after a kill =====

    [Fact]
    public void Uncommitted_ingest_leaves_no_postings_after_kill()
    {
        var db = OpenAndCreateIndex();
        var committed = Ingest(db, "durable content keepme9999");

        // Open a writer that indexes a property but never commits, then kill.
        var dirtyTx = db.BeginTransaction();
        var doomed = dirtyTx.CreateNode("Doc");
        dirtyTx.SetProperty(doomed, "body", PropertyValue.FromString("phantom doomed7777"));
        // NOTE: no Commit.

        Kill(db);

        using var reopened = Open();
        // Committed doc is intact; the uncommitted posting was undone (ARIES), so
        // the search result equals exactly the committed set.
        Search(reopened, "keepme9999").Should().ContainSingle().Which.Should().Be(committed);
        Search(reopened, "doomed7777").Should().BeEmpty("uncommitted postings must not survive a kill");
        using var rtx = reopened.BeginReadOnlyTransaction();
        rtx.NodeExists(doomed).Should().BeFalse();
    }

    // ===== (c) kill mid-uncommitted keeps committed prefix consistent =====

    [Fact]
    public void Kill_with_pending_ingest_keeps_committed_prefix_searchable()
    {
        var db = OpenAndCreateIndex();
        var keep = Ingest(db, "first committed doc retain5555");

        // A second writer adds a doc and leaves it uncommitted before the kill.
        var pendingTx = db.BeginTransaction();
        var pending = pendingTx.CreateNode("Doc");
        pendingTx.SetProperty(pending, "body", PropertyValue.FromString("second pending lose4444"));

        Kill(db);

        using var reopened = Open();
        Search(reopened, "retain5555").Should().ContainSingle().Which.Should().Be(keep);
        Search(reopened, "lose4444").Should().BeEmpty();
    }

    // ===== (d) 100-iteration repeated kill -> recover loop with FT ingest =====

    [Fact]
    public void RepeatedKillRecover_100_iterations_fulltext_consistent()
    {
        const int Iterations = 100;
        var ids = new List<NodeId>(Iterations);

        // First open creates the index; subsequent opens re-materialize it.
        var db = OpenAndCreateIndex();
        for (int i = 0; i < Iterations; i++)
        {
            // Everything indexed so far must still be searchable by its marker.
            for (int j = 0; j < ids.Count; j++)
            {
                Search(db, "mark" + j.ToString("D4"))
                    .Should().ContainSingle(
                        because: $"iteration {i}: doc {j} committed earlier must stay searchable")
                    .Which.Should().Be(ids[j]);
            }

            var n = Ingest(db, $"chunk body number {i} mark{i:D4} shared");
            ids.Add(n);

            Kill(db);
            db = Open();
        }

        // Final pass: all 100 markers resolve, and the shared term returns all 100.
        for (int j = 0; j < Iterations; j++)
        {
            Search(db, "mark" + j.ToString("D4")).Should().ContainSingle().Which.Should().Be(ids[j]);
        }
        Search(db, "shared", k: Iterations + 1).Should().HaveCount(Iterations);
        db.Dispose();
    }

    // ===== (e) update before kill: old term gone, new term searchable =====

    [Fact]
    public void Committed_update_before_kill_reindexes_after_recovery()
    {
        var db = OpenAndCreateIndex();
        NodeId doc = Ingest(db, "original oldterm1111 text");

        using (var tx = db.BeginTransaction())
        {
            tx.SetProperty(doc, "body", PropertyValue.FromString("revised newterm2222 text"));
            tx.Commit();
        }

        Kill(db);

        using var reopened = Open();
        Search(reopened, "oldterm1111").Should().BeEmpty("the superseded term's posting was removed at update time");
        Search(reopened, "newterm2222").Should().ContainSingle().Which.Should().Be(doc);
    }

    // ===== (f) FTS-7: torn commit — Commit record lost after body PageImages + FtLeafMutation =====

    /// <summary>
    /// FTS-7 (design 13 §10.4, レビュー CRITICAL): torn commit — FlushPending が body PageImage と eager
    /// FtLeafMutation を WAL へ書き終えた後、Commit レコードの前に crash。論理相 (RecoverLogical) は物理層と
    /// 同じ **presume-committed** (PageImage ∧ ¬Abort) で分類しなければならない。厳格 committed で分類した
    /// 修正前は、torn-commit した FT 取込 tx を loser 扱いして postings だけ消していた。
    ///
    /// 本テストの不変条件: torn-commit 後 recovery で例外を出さず、FT 検索結果がノード可視性と **整合**する
    /// (可視なら検索可 / 不可視なら検索不可)。決定論的注入: committed データを flush で disk へ落とし、WAL 末尾の
    /// Commit レコードを truncate。なお本エンジンの torn-commit body は Pass 2a (厳格 redo) では復元されず
    /// (= ノードは不可視になりがち) だが、本テストは「postings 単独で消えて整合が崩れる」修正前の不整合が
    /// 起きないことを検証する (presume-committed で body/postings が同じ運命をたどる)。
    /// </summary>
    [Fact]
    public void TornCommit_fulltext_recovery_keeps_postings_consistent_with_node()
    {
        var db = OpenAndCreateIndex();
        NodeId baseDoc = Ingest(db, "durable base baseword0001");
        NodeId torn = Ingest(db, "tornword7777 committed content");
        // committed データ (node/property + Suppressed FT leaf) を disk へ flush し torn-commit の前提を作る。
        ((BinaryGraphStorageBackend)db.BackendInternal).FlushDataPagesForTest();
        // 未コミットの writer を 1 つ開いたまま kill すると WAL がクリーン削除されず torn 注入できる。
        var keepWalAlive = db.BeginTransaction();
        keepWalAlive.CreateNode("Doc");
        Kill(db);

        // 末尾 (= torn doc) の Commit レコードを 1 件削る (これより後ろの未コミット tx 記録も落ちるが無害)。
        // 残るのは torn doc の body PageImage + FtLeafMutation = torn-commit 窓。
        TruncateTrailingCommitRecord(_path + "-wal");

        using var reopened = Open();
        // 不変条件: 「torn doc がノードとして可視」⇔「torn doc が検索可能」(presume-committed の body/postings
        // が同じ運命をたどる)。修正前は body は presume-committed 側で残るのに postings だけ loser undo で消え、
        // 「可視ノードなのに検索不能」という不整合 (= false の左辺・空の右辺) を生んでいた。
        bool nodeVisible;
        using (var rtx = reopened.BeginReadOnlyTransaction())
            nodeVisible = rtx.NodeExists(torn);
        bool searchable = Search(reopened, "tornword7777").Contains(torn);
        searchable.Should().Be(nodeVisible,
            "torn-commit recovery must keep FT postings consistent with node visibility (presume-committed)");
        // committed prefix (base doc) は無傷。
        Search(reopened, "baseword0001").Should().ContainSingle().Which.Should().Be(baseDoc);
    }

    // ===== (g) 監査 #1: loser 論理 undo は committed キーを clobber してはならない =====

    /// <summary>
    /// 監査 #1 (recovery clobber, spec: 02_wal_recovery.md#two-phase-recovery): recovery Pass 3 の loser 論理 undo は、
    /// その後コミットされた tx が同じ postings キーへ加えた変更を上書き (clobber) してはならない。
    ///
    /// FtLeafMutation は state-setting で、Delete レコードは undo 再挿入用に旧値を載せる
    /// (<see cref="FtLeafMutationCodec"/>)。よって loser の <c>Delete(K)</c> を Pass 3 が逆適用すると
    /// <c>UpsertRaw(K, 旧値)</c> を無条件実行し、Pass 2b が確立した committed 値を旧値で上書きする。
    ///
    /// シナリオ (単一ライタ):
    ///   tx1: E1="alice"            commit  ((alice,E1) postings)
    ///   tx2: E1="bob"  → Rollback  (aborted loser; WAL に Delete(alice,E1) が eager 記録される)
    ///   tx3: E1="charlie"          commit  (Delete(alice,E1) + Upsert(charlie,E1))
    ///   kill → recover
    /// committed 最終状態は E1="charlie" なので "alice" は検索ヒットしてはならない。修正前は Pass 3 が
    /// tx2 の Delete(alice,E1) を UpsertRaw で逆適用し "alice" を復活させ、committed の削除を clobber する。
    /// </summary>
    [Fact]
    public void AbortedTx_logical_undo_must_not_clobber_committed_key_after_recovery()
    {
        var db = OpenAndCreateIndex();
        NodeId e1 = Ingest(db, "alice");

        // tx2: "alice" posting を削除する更新 → ロールバック (in-process で "alice" を復元)。
        // ただし eager FtLeafMutation Delete(alice,E1) は WAL に残り、recovery で loser undo 対象になる。
        using (var tx2 = db.BeginTransaction())
        {
            tx2.SetProperty(e1, "body", PropertyValue.FromString("bob"));
            tx2.Rollback();
        }

        // tx3: "charlie" へ更新して commit。committed 最終状態は alice 無し / charlie 有り。
        using (var tx3 = db.BeginTransaction())
        {
            tx3.SetProperty(e1, "body", PropertyValue.FromString("charlie"));
            tx3.Commit();
        }

        // 未コミット writer を開いたまま kill して checkpoint truncate を防ぎ、recovery で論理相を必ず通す。
        var keepWalAlive = db.BeginTransaction();
        keepWalAlive.CreateNode("Doc");
        Kill(db);

        using var reopened = Open();
        Search(reopened, "charlie").Should().ContainSingle().Which.Should().Be(e1,
            "committed final state E1=\"charlie\" must be searchable after recovery");
        Search(reopened, "alice").Should().BeEmpty(
            "aborted tx's logical undo must not resurrect a key the committed state removed (audit #1 clobber)");
    }

    // ===== (h) 監査 #2: RollbackTo(savepoint) は FT 論理変更を巻き戻す (in-process) =====

    /// <summary>
    /// 監査 #2 (FT savepoint undo, spec: 03_mvcc.md#ft-logical-undo): savepoint への部分ロールバックは、
    /// savepoint 以降に発行された FT leaf 論理ミューテーションも巻き戻さねばならない。
    ///
    /// シナリオ (単一 tx 内):
    ///   tx1: E1="alice"                 commit  (alice→E1)
    ///   tx2: sp=Savepoint()
    ///        E1="bob"   (alice posting 削除 / bob posting 挿入)
    ///        RollbackTo(sp)   ← page (E1.body) は "alice" に戻る
    ///        commit
    /// committed 最終状態は E1="alice" なので "alice" がヒットし "bob" はヒットしてはならない。
    /// 修正前は RollbackTo が FT 論理 undo を呼ばず、postings が bob→E1 のまま commit され、
    /// 生きている E1 への false-positive ("bob") + 取りこぼし ("alice") が発生する。
    /// </summary>
    [Fact]
    public void RollbackToSavepoint_undoes_fulltext_mutations_in_process()
    {
        var db = OpenAndCreateIndex();
        NodeId e1 = Ingest(db, "alice");

        using (var tx = db.BeginTransaction())
        {
            var sp = tx.Savepoint();
            tx.SetProperty(e1, "body", PropertyValue.FromString("bob"));
            tx.RollbackTo(sp);
            tx.Commit();
        }

        Search(db, "alice").Should().ContainSingle().Which.Should().Be(e1,
            "RollbackTo restored E1.body=\"alice\" so the alice posting must be present");
        Search(db, "bob").Should().BeEmpty(
            "RollbackTo must undo the savepoint's FT mutations (audit #2): the bob posting was rolled back");
        db.Dispose();
    }

    // ===== (i) 監査 #2: 上記が crash 後も保たれる (WAL 補償レコード) =====

    /// <summary>
    /// 監査 #2 (crash 経路): FtLeafMutation は eager に WAL へ追記されるため、savepoint で破棄された
    /// 変更も commit した tx の WAL に残る。RollbackTo は逆操作を **補償 FtLeafMutation** として WAL へ
    /// 追記し、recovery Pass 2b が forward→補償で正しい (ロールバック後の) 状態へ収束しなければならない。
    /// in-process 巻き戻しだけでは crash 後に破棄分が Pass 2b で蘇る。
    /// </summary>
    [Fact]
    public void RollbackToSavepoint_fulltext_undo_survives_kill()
    {
        var db = OpenAndCreateIndex();
        NodeId e1 = Ingest(db, "alice");

        using (var tx = db.BeginTransaction())
        {
            var sp = tx.Savepoint();
            tx.SetProperty(e1, "body", PropertyValue.FromString("bob"));
            tx.RollbackTo(sp);
            tx.Commit();
        }

        // checkpoint truncate を防いで recovery の論理相を必ず通す。
        var keepWalAlive = db.BeginTransaction();
        keepWalAlive.CreateNode("Doc");
        Kill(db);

        using var reopened = Open();
        Search(reopened, "alice").Should().ContainSingle().Which.Should().Be(e1,
            "committed final state E1=\"alice\" must be searchable after recovery");
        Search(reopened, "bob").Should().BeEmpty(
            "rolled-back FT mutation must not resurface after recovery (audit #2 WAL compensator)");
    }

    // ===== (j) 監査 #2: rollback 後に full abort しても二重破壊しない =====

    /// <summary>
    /// 監査 #2 (相互作用): savepoint 以降の FT mutation を RollbackTo で巻き戻した後、tx 全体を abort する。
    /// RollbackTo の補償レコードは undo スタックに積まないため、full abort は savepoint 以前の mutation のみ
    /// 巻き戻す (補償を再 undo して蘇らせない)。最終的に tx 開始前の committed 状態に戻る。
    ///   tx1: E1="alice"               commit
    ///   tx2: sp=Savepoint(); E1="bob"; RollbackTo(sp); E1="charlie"; Rollback() (full abort)
    /// 期待: E1 は committed の "alice" のまま (charlie も bob も無し)。kill を挟んでも同じ。
    /// </summary>
    [Fact]
    public void RollbackToSavepoint_then_full_abort_restores_committed_state()
    {
        var db = OpenAndCreateIndex();
        NodeId e1 = Ingest(db, "alice");

        var tx = db.BeginTransaction();
        var sp = tx.Savepoint();
        tx.SetProperty(e1, "body", PropertyValue.FromString("bob"));
        tx.RollbackTo(sp);
        tx.SetProperty(e1, "body", PropertyValue.FromString("charlie"));
        tx.Rollback(); // full abort

        // 未コミット writer で WAL を残し、recovery 経路も通す。
        var keepWalAlive = db.BeginTransaction();
        keepWalAlive.CreateNode("Doc");
        Kill(db);

        using var reopened = Open();
        Search(reopened, "alice").Should().ContainSingle().Which.Should().Be(e1,
            "aborting the whole tx restores the committed E1=\"alice\"");
        Search(reopened, "bob").Should().BeEmpty("savepoint-discarded term must not survive");
        Search(reopened, "charlie").Should().BeEmpty("aborted post-savepoint term must not survive");
    }

    // ===== (k) 監査 #2: nested savepoint + ReleaseSavepoint =====

    /// <summary>
    /// 監査 #2 (nested + release): 2 段 savepoint。内側を Release して外側へマージ後、外側へ RollbackTo すると
    /// マージされた内側分も巻き戻る (Release は savepoint を消費するが変更は親バケットに残るため)。
    ///   tx1: E1="alice" commit
    ///   tx2: sp1; E1="bob"; sp2; E1="carol"; Release(sp2); RollbackTo(sp1); commit
    /// 期待: committed の "alice" に戻る (bob/carol は sp1 以降なので全て巻き戻る)。
    /// </summary>
    [Fact]
    public void NestedSavepoint_release_then_rollback_undoes_merged_fulltext()
    {
        var db = OpenAndCreateIndex();
        NodeId e1 = Ingest(db, "alice");

        using (var tx = db.BeginTransaction())
        {
            var sp1 = tx.Savepoint();
            tx.SetProperty(e1, "body", PropertyValue.FromString("bob"));
            var sp2 = tx.Savepoint();
            tx.SetProperty(e1, "body", PropertyValue.FromString("carol"));
            tx.ReleaseSavepoint(sp2);
            tx.RollbackTo(sp1);
            tx.Commit();
        }

        Search(db, "alice").Should().ContainSingle().Which.Should().Be(e1,
            "RollbackTo(sp1) rolls back everything after sp1, including the released sp2 work");
        Search(db, "bob").Should().BeEmpty();
        Search(db, "carol").Should().BeEmpty();
        db.Dispose();
    }

    // WAL 末尾の Commit レコードを 1 件削って torn commit を作る。レコード形式:
    // [length:4][lsn:8][txId:8][type:1][crc:4][payload] (WriteAheadLog.EncodeRecord)。
    private static void TruncateTrailingCommitRecord(string walPath)
    {
        byte[] bytes = File.ReadAllBytes(walPath);
        const int headerSize = 25;
        long lastCommit = -1;
        int pos = 0;
        while (pos + headerSize <= bytes.Length)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos));
            if (len < headerSize || pos + len > bytes.Length) break;
            if ((WalRecordType)bytes[pos + 20] == WalRecordType.Commit) lastCommit = pos;
            pos += len;
        }
        if (lastCommit < 0) throw new InvalidOperationException("no Commit record in WAL to truncate");
        using var fs = new FileStream(walPath, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.SetLength(lastCommit);
    }
}
