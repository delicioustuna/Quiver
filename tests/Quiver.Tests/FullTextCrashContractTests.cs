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
/// Backend scope: full-text indexes are a binary-backend feature; the SQLite
/// backend reports <c>CreateFullTextIndex</c> as NotSupported (FTS-2 decision B,
/// covered by Quiver.Storage.Sqlite.Tests). The "両 backend" crash requirement
/// therefore resolves to "binary backend runs the full FT crash loop; SQLite's
/// contract is the NotSupported guard". This suite is the binary half.
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
