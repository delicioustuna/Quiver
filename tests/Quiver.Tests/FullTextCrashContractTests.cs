using System.Buffers.Binary;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 全文インデックスの永続性契約を <see cref="QuiverDatabase"/> と
/// <c>g.Search</c> のエンジンレベルで検証する。
/// コミット、プロセス停止の模擬、再オープン、リカバリーの順に実行し、
/// 検索結果がコミット済み文書集合と一致することを確認する。
/// Postings と Norms は通常の B+Tree なので、QUIVER-SW の PageImage WAL で復旧する。
///
/// Windows では停止した同一プロセスが排他的ファイルハンドルを再利用できないため、
/// ハンドルの破棄とファイナライザーの強制実行でプロセス停止を模擬してから再オープンする。
/// <see cref="IWriteTransaction.Commit"/> が完了した変更は復旧し、
/// 未コミットの変更は復活しないことを確認する。
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

    private QuiverDatabase Open() => QuiverDatabase.Open(_path);

    /// <summary>
    /// 新しいデータベースを開いて全文インデックスを作成する。
    /// シナリオの初回だけ使用し、再オープン時は <see cref="Open"/> が
    /// カタログからインデックスを再構築する。
    /// </summary>
    private QuiverDatabase OpenAndCreateIndex()
    {
        var db = Open();
        db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(Index, new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
        return db;
    }

    /// <summary>正常終了時の完全なフラッシュを行わずにハンドルを破棄し、ファイナライザーを実行してプロセス停止を模擬する。</summary>
    private static void Kill(QuiverDatabase db)
    {
        try { db.Dispose(); } catch { /* 終了途中の処理が失われる状況を再現する。 */ }
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

    private static VertexId Ingest(QuiverDatabase db, string body)
    {
        using var tx = db.BeginWriteTransaction();
        var n = tx.CreateVertex("Doc");
        tx.SetProperty(n, "body", PropertyValue.FromString(body));
        tx.Commit();
        return n;
    }

    private static List<VertexId> Search(QuiverDatabase db, string query, int k = 10)
    {
        using var rtx = db.BeginReadTransaction();
        return rtx.Query.Search(Index, query, k).ToList();
    }

    // ===== (a) committed full-text docs survive a kill and stay searchable =====

    [Fact]
    public void Committed_docs_survive_kill_and_remain_searchable()
    {
        VertexId alpha, beta, gamma;
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
        var dirtyTx = db.BeginWriteTransaction();
        var doomed = dirtyTx.CreateVertex("Doc");
        dirtyTx.SetProperty(doomed, "body", PropertyValue.FromString("phantom doomed7777"));
        // NOTE: no Commit.

        Kill(db);

        using var reopened = Open();
        // Committed doc is intact; the uncommitted PageImage is not replayed.
        Search(reopened, "keepme9999").Should().ContainSingle().Which.Should().Be(committed);
        Search(reopened, "doomed7777").Should().BeEmpty("uncommitted postings must not survive a kill");
        using var rtx = reopened.BeginReadTransaction();
        rtx.VertexExists(doomed).Should().BeFalse();
    }

    // ===== (c) kill mid-uncommitted keeps committed prefix consistent =====

    [Fact]
    public void Kill_with_pending_ingest_keeps_committed_prefix_searchable()
    {
        var db = OpenAndCreateIndex();
        var keep = Ingest(db, "first committed doc retain5555");

        // A second writer adds a doc and leaves it uncommitted before the kill.
        var pendingTx = db.BeginWriteTransaction();
        var pending = pendingTx.CreateVertex("Doc");
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
        var ids = new List<VertexId>(Iterations);

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
        VertexId doc = Ingest(db, "original oldterm1111 text");

        using (var tx = db.BeginWriteTransaction())
        {
            tx.SetProperty(doc, "body", PropertyValue.FromString("revised newterm2222 text"));
            tx.Commit();
        }

        Kill(db);

        using var reopened = Open();
        Search(reopened, "oldterm1111").Should().BeEmpty("the superseded term's posting was removed at update time");
        Search(reopened, "newterm2222").Should().ContainSingle().Which.Should().Be(doc);
    }

    // ===== コミットレコードだけが失われた不完全コミット =====

    /// <summary>
    /// Vertex body と全文 B+Tree の PageImage に続く Commit record を途中で切り詰める。
    /// checksum を検証できない Commit を無視して開くと primary と posting の可視性が分かれうるため、
    /// open は通常 operation を受け付ける前に corruption として拒否する。
    /// </summary>
    [Fact]
    public void TornCommit_record_is_rejected_before_fulltext_open()
    {
        var db = OpenAndCreateIndex();
        _ = Ingest(db, "durable base baseword0001");
        _ = Ingest(db, "tornword7777 committed content");
        // committed データを disk へ flush し torn-commit の前提を作る。
        ((BinaryGraphStorageBackend)db.BackendInternal).FlushDataPagesForTest();
        // 未コミットの writer を 1 つ開いたまま kill すると WAL がクリーン削除されず torn 注入できる。
        var keepWalAlive = db.BeginWriteTransaction();
        keepWalAlive.CreateVertex("Doc");
        Kill(db);

        // 末尾 (= torn doc) の Commit レコードを途中で切る。
        // checksum を検証できない WAL tail は有効な prefix として受理しない。
        TruncateTrailingCommitRecord(_path + "-wal");

        ((Action)(() => Open()))
            .Should().Throw<CorruptionException>();
    }

    // ===== abort 後の PageImage は committed key を上書きしない =====

    /// <summary>
    /// abort した transaction の PageImage が、その後 commit された同じ postings key を上書きしないことを検証する。
    /// シナリオ:
    ///   tx1: E1="alice"            commit  ((alice,E1) postings)
    ///   tx2: E1="bob"  -> Rollback
    ///   tx3: E1="charlie"          commit
    ///   kill → recover
    /// committed 最終状態は E1="charlie" なので "alice" は検索ヒットしてはならない。
    /// </summary>
    [Fact]
    public void AbortedTx_page_image_must_not_clobber_committed_key_after_recovery()
    {
        var db = OpenAndCreateIndex();
        VertexId e1 = Ingest(db, "alice");

        // tx2 は in-process before-image で rollback する。
        using (var tx2 = db.BeginWriteTransaction())
        {
            tx2.SetProperty(e1, "body", PropertyValue.FromString("bob"));
            tx2.Rollback();
        }

        // tx3: "charlie" へ更新して commit。committed 最終状態は alice 無し / charlie 有り。
        using (var tx3 = db.BeginWriteTransaction())
        {
            tx3.SetProperty(e1, "body", PropertyValue.FromString("charlie"));
            tx3.Commit();
        }

        // 未コミット writer を開いたまま kill して checkpoint truncate を防ぐ。
        var keepWalAlive = db.BeginWriteTransaction();
        keepWalAlive.CreateVertex("Doc");
        Kill(db);

        using var reopened = Open();
        Search(reopened, "charlie").Should().ContainSingle().Which.Should().Be(e1,
            "committed final state E1=\"charlie\" must be searchable after recovery");
        Search(reopened, "alice").Should().BeEmpty(
            "aborted tx must not resurrect a key the committed state removed");
    }

    // ===== RollbackTo(savepoint) は全文 B+Tree のページ変更も巻き戻す =====

    /// <summary>
    /// savepoint への部分 rollback は、savepoint 以降の全文 B+Tree ページ変更も巻き戻す。
    ///
    /// シナリオ (単一 tx 内):
    ///   tx1: E1="alice"                 commit  (alice→E1)
    ///   tx2: sp=Savepoint()
    ///        E1="bob"   (alice posting 削除 / bob posting 挿入)
    ///        RollbackTo(sp)   ← page (E1.body) は "alice" に戻る
    ///        commit
    /// committed 最終状態は E1="alice" なので "alice" がヒットし "bob" はヒットしてはならない。
    /// </summary>
    [Fact]
    public void RollbackToSavepoint_undoes_fulltext_mutations_in_process()
    {
        var db = OpenAndCreateIndex();
        VertexId e1 = Ingest(db, "alice");

        using (var tx = db.BeginWriteTransaction())
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

    // ===== savepoint rollback 後の PageImage が crash recovery でも保たれる =====

    /// <summary>
    /// RollbackTo 後に commit した PageImage が rollback 後の状態を表し、crash recovery でも破棄分が蘇らないことを検証する。
    /// </summary>
    [Fact]
    public void RollbackToSavepoint_fulltext_undo_survives_kill()
    {
        var db = OpenAndCreateIndex();
        VertexId e1 = Ingest(db, "alice");

        using (var tx = db.BeginWriteTransaction())
        {
            var sp = tx.Savepoint();
            tx.SetProperty(e1, "body", PropertyValue.FromString("bob"));
            tx.RollbackTo(sp);
            tx.Commit();
        }

        // checkpoint truncate を防いで recovery を通す。
        var keepWalAlive = db.BeginWriteTransaction();
        keepWalAlive.CreateVertex("Doc");
        Kill(db);

        using var reopened = Open();
        Search(reopened, "alice").Should().ContainSingle().Which.Should().Be(e1,
            "committed final state E1=\"alice\" must be searchable after recovery");
        Search(reopened, "bob").Should().BeEmpty(
            "rolled-back FT mutation must not resurface after recovery");
    }

    // ===== (j) 監査 #2: rollback 後に full abort しても二重破壊しない =====

    /// <summary>
    /// savepoint 以降の全文 B+Tree 更新を RollbackTo で戻した後、transaction 全体を abort する。
    /// 最終的に transaction 開始前の committed 状態へ戻る。
    ///   tx1: E1="alice"               commit
    ///   tx2: sp=Savepoint(); E1="bob"; RollbackTo(sp); E1="charlie"; Rollback() (full abort)
    /// 期待: E1 は committed の "alice" のまま (charlie も bob も無し)。kill を挟んでも同じ。
    /// </summary>
    [Fact]
    public void RollbackToSavepoint_then_full_abort_restores_committed_state()
    {
        var db = OpenAndCreateIndex();
        VertexId e1 = Ingest(db, "alice");

        var tx = db.BeginWriteTransaction();
        var sp = tx.Savepoint();
        tx.SetProperty(e1, "body", PropertyValue.FromString("bob"));
        tx.RollbackTo(sp);
        tx.SetProperty(e1, "body", PropertyValue.FromString("charlie"));
        tx.Rollback(); // full abort

        // 未コミット writer で WAL を残し、recovery 経路も通す。
        var keepWalAlive = db.BeginWriteTransaction();
        keepWalAlive.CreateVertex("Doc");
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
        VertexId e1 = Ingest(db, "alice");

        using (var tx = db.BeginWriteTransaction())
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

    // WAL 末尾の Commit レコードを途中で切って torn commit を作る。レコード形式:
    // [length:4][lsn:8][txId:8][type:1][crc:4][payload] (WriteAheadLog.EncodeRecord)。
    private static void TruncateTrailingCommitRecord(string walPath)
    {
        byte[] bytes = File.ReadAllBytes(walPath);
        const int headerSize = 25;
        long lastCommit = -1;
        int pos = WalFormat.FileHeaderSize;
        while (pos + headerSize <= bytes.Length)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos));
            if (len < headerSize || pos + len > bytes.Length) break;
            if ((WalRecordType)bytes[pos + 20] == WalRecordType.Commit) lastCommit = pos;
            pos += len;
        }
        if (lastCommit < 0) throw new InvalidOperationException("no Commit record in WAL to truncate");
        using var fs = new FileStream(walPath, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.SetLength(lastCommit + headerSize / 2);
    }
}
