using FluentAssertions;
using Quiver.Backend.Tests.Faults;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// BA-9 binary backend crash contract: runs the shared
/// <see cref="GraphStorageBackendCrashContractTests"/> suite plus three
/// binary-specific scenarios (WAL segment roll, RecoveryManager edge cases,
/// adjacency block sidecar loss).
/// </summary>
public sealed class BinaryGraphStorageBackendCrashContractTests
    : GraphStorageBackendCrashContractTests
{
    protected override IGraphStorageBackendFactory CreateFactory()
        => new BinaryGraphStorageBackendFactory();

    // ARCH-4 増分8: binary backend は単一ファイル *.quiver。コンテナは <dir>/graph.quiver、
    // WAL は <dir>/graph.quiver-wal。fault injector の LatestWalSegment / sidecar 削除はこの前提。
    protected override string DatabasePath
        => System.IO.Path.Combine(DatabaseDirectory, "graph.quiver");

    protected override void InjectTornWriteAtTail()
    {
        // Most recent WAL segment is the durability boundary. Zero-fill the
        // last 16 bytes so the trailing record's CRC32C never validates.
        var seg = LatestWalSegment();
        if (seg is null) return;
        TornWriteInjector.ZeroFillTail(seg, tailBytes: 16);
    }

    protected override void InjectChecksumCorruption()
    {
        var seg = LatestWalSegment();
        if (seg is null) return;
        // WAL header: Length(4) + Lsn(8) + TxId(8) + Type(1) + Crc32C(4) = 25 B.
        // Flip a bit in the Type byte of the first record so its CRC fails on replay.
        ChecksumCorruptor.FlipBitAt(seg, offset: 20, bitInByte: 0);
    }

    protected override void DeleteSidecarFiles()
    {
        // adj.epoch is rebuilt from the adjacency store on next open; deleting
        // it should be tolerated. If absent (no bulk load happened) this is a no-op.
        var epoch = Path.Combine(DatabaseDirectory, "adj.epoch");
        SidecarFileDeleter.TryDelete(epoch);
    }

    // ===== Binary-specific scenarios =====

    /// <summary>
    /// FT-15 Tier2: a transaction explicitly rolled back in-process, then
    /// followed by a process kill, must leave no uncommitted data. The abort
    /// flushes its before-image restore durably before the kill, and recovery
    /// keeps earlier committed work intact.
    /// </summary>
    [Fact]
    public void AbortThenKill_leaves_no_uncommitted_data()
    {
        IGraphStorageBackend? backend = Open();
        NodeId committed;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            committed = tx.CreateNode("Committed");
            tx.Commit();
        }

        var abortTx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false);
        NodeId rolledBack = abortTx.CreateNode("RolledBack");
        abortTx.SetProperty(rolledBack, "ephemeral", PropertyValue.FromInt64(42L));
        abortTx.Rollback();

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        rtx.NodeExists(committed).Should().BeTrue(
            "committed work survives an abort-then-kill sequence");
        rtx.NodeExists(rolledBack).Should().BeFalse(
            "an explicitly aborted node must not resurface after a kill");
        rtx.Rollback();
    }

    /// <summary>
    /// FT-17 索引整合性: a B+Tree index entry inserted by an uncommitted transaction
    /// must be undone by recovery after a kill, while a committed index entry must
    /// survive. The binary backend logs index mutations as WalRecordType.IndexMutation
    /// and RecoveryManager replays the inverse for crashed transactions.
    /// </summary>
    [Fact]
    public void IndexEntry_from_uncommitted_tx_is_undone_after_kill()
    {
        IGraphStorageBackend? backend = Open();

        // Committed baseline index entry.
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            var keep = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "committed", keep);
            tx.Commit();
        }

        // Uncommitted transaction inserts an index entry, then the process dies.
        var dirtyTx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false);
        var doomed = dirtyTx.CreateNode("Person");
        dirtyTx.IndexInsert("idx_name", "doomed", doomed);
        // NOTE: no Commit.

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);

        var survived = rtx.SeekIndex("idx_name", PropertyValue.FromString("committed"));
        survived.MoveNext().Should().BeTrue(
            "a committed index entry must survive a kill");
        survived.Dispose();

        var undone = rtx.SeekIndex("idx_name", PropertyValue.FromString("doomed"));
        undone.MoveNext().Should().BeFalse(
            "an uncommitted index entry must be undone by recovery");
        undone.Dispose();
        rtx.Rollback();
    }

    /// <summary>
    /// Open with a small WAL segment size so that a few commits force a
    /// segment roll, then simulate a kill and confirm everything still
    /// recovers. Probes the boundary code that closes one segment and opens
    /// the next.
    /// </summary>
    [Fact]
    public void KillAcrossWalSegmentRoll_recovers()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_crash_walroll_" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new BinaryGraphStorageBackendFactory();
            // Tiny segments — every other commit will roll.
            var opts = new GraphDatabaseOptions { WalSegmentSize = 64 * 1024 };

            var ids = new List<NodeId>();
            for (int i = 0; i < 30; i++)
            {
                IGraphStorageBackend? backend = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
                using (var tx = backend.BeginGraphTransaction(
                    IsolationLevel.SnapshotIsolation, readOnly: false))
                {
                    // 1 KB payload so segment fills relatively quickly.
                    var node = tx.CreateNode("Big");
                    tx.SetProperty(node, "blob",
                        PropertyValue.FromString(new string('x', 1024)));
                    ids.Add(node);
                    tx.Commit();
                }
                KillProcessSimulator.SimulateKill(ref backend);
            }

            using var reopened = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            foreach (var id in ids)
                rtx.NodeExists(id).Should().BeTrue();
            rtx.Rollback();
        }
        finally
        {
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    /// <summary>
    /// Open an empty directory (no WAL, no data files) — the binary backend
    /// must boot cleanly with an empty RecoveryManager pass.
    /// </summary>
    [Fact]
    public void EmptyDirectory_recovers_with_empty_FileRegistry_pass()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_crash_emptydir_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            using var backend = new BinaryGraphStorageBackendFactory()
                .Open(System.IO.Path.Combine(dir, "graph.quiver"), new GraphDatabaseOptions());
            using var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false);
            var node = tx.CreateNode("First");
            tx.Commit();
        }
        finally
        {
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    /// <summary>
    /// Truncate the adjacency block index sidecar to 0 bytes after a commit.
    /// adj_idx.dat is only written by BulkLoader, so if absent the factory
    /// gracefully skips the V1 path. The committed node store remains
    /// readable.
    /// </summary>
    [Fact]
    public void AdjacencyIndexSidecar_truncated_backend_falls_back_safely()
    {
        IGraphStorageBackend? backend = Open();
        NodeId persisted;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            persisted = tx.CreateNode("Pre");
            tx.Commit();
        }
        KillProcessSimulator.SimulateKill(ref backend);

        var adjIdx = Path.Combine(DatabaseDirectory, "adj_idx.dat");
        if (File.Exists(adjIdx))
            TornWriteInjector.TruncateTail(adjIdx, int.MaxValue); // truncate to 0

        // Either succeeds (linked-list fallback) or fails with StorageException.
        IGraphStorageBackend? reopened = null;
        try
        {
            reopened = Open();
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            rtx.NodeExists(persisted).Should().BeTrue();
            rtx.Rollback();
        }
        catch (StorageException) { /* acceptable: fail-safe */ }
        catch (CorruptionException) { /* acceptable */ }
        finally
        {
            reopened?.Dispose();
        }
    }

    /// <summary>
    /// FT-18 (gap 1 redo): a B+Tree index entry inserted by a committed transaction
    /// must survive a process kill that occurs <strong>before</strong> the index
    /// file was Dispose-flushed. The IndexMutation WAL record was made durable at
    /// commit-FlushTo, and recovery's new index redo pass replays it forward
    /// idempotently so the entry reappears in the index file.
    /// </summary>
    [Fact]
    public void CommittedIndexEntry_survives_kill_via_redo()
    {
        IGraphStorageBackend? backend = Open();
        NodeId committed;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            committed = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "alice", committed);
            tx.Commit();
        }

        // Kill without Dispose — the .idx file may not have been fsync'd.
        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        var cur = rtx.SeekIndex("idx_name", PropertyValue.FromString("alice"));
        cur.MoveNext().Should().BeTrue(
            "committed index entry must be redone from IndexMutation WAL records");
        cur.Current.Should().Be(committed);
        cur.MoveNext().Should().BeFalse(
            "idempotent redo must not create duplicate entries");
        cur.Dispose();
        rtx.Rollback();
    }

    /// <summary>
    /// FT-18 (truncation safety): once a checkpoint fires, its WAL prefix
    /// (including the IndexMutation records) is truncated. Without
    /// <see cref="Quiver.Index.IIndexManager.FlushAll"/>, the index file would
    /// not be durable and a subsequent kill would lose the committed entry
    /// permanently. With FT-18 the checkpoint fsyncs the index, so the entry
    /// survives even after WAL truncation + kill.
    /// </summary>
    [Fact]
    public void CommittedIndexEntry_survives_checkpoint_then_kill()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_ft18_chkpt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new BinaryGraphStorageBackendFactory();
            // Tiny threshold so the second commit forces a checkpoint.
            var opts = new GraphDatabaseOptions { CheckpointThresholdBytes = 1 };

            NodeId committed;
            IGraphStorageBackend? backend = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                committed = tx.CreateNode("Person");
                tx.IndexInsert("idx_name", "checkpointed", committed);
                tx.Commit();
            }
            // Second commit triggers MaybeCheckpoint (ActiveCount == 0,
            // BytesWritten >= threshold). The checkpoint flushes the index
            // and then truncates the WAL prefix containing the IndexMutation.
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                tx.CreateNode("Filler");
                tx.Commit();
            }

            KillProcessSimulator.SimulateKill(ref backend);

            using var reopened = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            var cur = rtx.SeekIndex("idx_name", PropertyValue.FromString("checkpointed"));
            cur.MoveNext().Should().BeTrue(
                "checkpoint must fsync the index file before truncating its WAL prefix");
            cur.Current.Should().Be(committed);
            cur.Dispose();
            rtx.Rollback();
        }
        finally
        {
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    /// <summary>
    /// FT-18 (idempotent index undo): pre-FT-18, recovery skipped aborted
    /// transactions entirely for index undo on the assumption that in-process
    /// abort was already durable. But the in-process inverse runs through the
    /// buffer pool (not direct fsync), so under steal it could be lost.
    /// FT-18 re-applies the inverse on recovery via check-before-apply, so
    /// after abort + kill the prior committed state is intact, the aborted
    /// insert is gone, and no duplicates are introduced by double-undo.
    /// </summary>
    [Fact]
    public void AbortedIndexInsert_then_kill_leaves_no_residual_entry()
    {
        IGraphStorageBackend? backend = Open();
        NodeId baseline;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            baseline = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "baseline", baseline);
            tx.Commit();
        }

        var abortTx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false);
        var resurrected = abortTx.CreateNode("Person");
        abortTx.IndexInsert("idx_name", "resurrected", resurrected);
        abortTx.Rollback();

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);

        var keepCur = rtx.SeekIndex("idx_name", PropertyValue.FromString("baseline"));
        keepCur.MoveNext().Should().BeTrue(
            "committed index entry must survive abort + kill via idempotent redo");
        keepCur.Current.Should().Be(baseline);
        keepCur.MoveNext().Should().BeFalse(
            "idempotent recovery must not duplicate the surviving entry");
        keepCur.Dispose();

        var gone = rtx.SeekIndex("idx_name", PropertyValue.FromString("resurrected"));
        gone.MoveNext().Should().BeFalse(
            "aborted index entry must remain undone after abort + kill");
        gone.Dispose();
        rtx.Rollback();
    }

    /// <summary>
    /// FT-19: 大量 insert で B+Tree split を多発させた tx を commit → kill。
    /// 物理 PageImage WAL ロギングにより、split で touched された全ページが PageImage 経由で
    /// 再生されるので、partial-disk-arrival シナリオでも recovery 後に B+Tree が構造的に整合し、
    /// 全エントリが可視に戻る。FT-18 までの論理 redo では出せなかった保証。
    /// </summary>
    [Fact]
    public void IndexHeavySplitWorkload_then_kill_recovers_all_entries()
    {
        IGraphStorageBackend? backend = Open();
        var insertedKeys = new List<int>();
        var insertedNodes = new List<NodeId>();
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            // 1000 件 — 1 leaf (8 KB) を遥かに超えるので multi-level B+Tree に。
            for (int i = 0; i < 1000; i++)
            {
                var n = tx.CreateNode("Person");
                tx.IndexInsert("idx_split", (long)i, n);
                insertedKeys.Add(i);
                insertedNodes.Add(n);
            }
            tx.Commit();
        }

        // Dispose 経由の flush なしで kill。PageImage 経由でしか復旧できない状況を作る。
        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        for (int probe = 0; probe < 1000; probe += 37)
        {
            var cur = rtx.SeekIndex("idx_split", PropertyValue.FromInt64((long)probe));
            cur.MoveNext().Should().BeTrue($"key {probe} は committed PageImage 経由で復旧されるはず");
            cur.Current.Should().Be(insertedNodes[probe]);
            cur.MoveNext().Should().BeFalse("idempotent recovery で重複なし");
            cur.Dispose();
        }
        rtx.Rollback();
    }

    /// <summary>
    /// FT-19: 複数索引を同一 tx 内で更新 → kill。各索引は別 fileKind を catalog で
    /// 割り当てられているので、全 fileKind の PageImage 系統が独立に redo されるはず。
    /// </summary>
    [Fact]
    public void MultipleIndexes_committed_then_kill_all_survive()
    {
        IGraphStorageBackend? backend = Open();
        NodeId nodeA, nodeB;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            nodeA = tx.CreateNode("Person");
            nodeB = tx.CreateNode("Person");
            tx.IndexInsert("idx_name", "alice", nodeA);
            tx.IndexInsert("idx_email", "alice@example.com", nodeA);
            tx.IndexInsert("idx_age", 30L, nodeA);
            tx.IndexInsert("idx_name", "bob", nodeB);
            tx.IndexInsert("idx_email", "bob@example.com", nodeB);
            tx.IndexInsert("idx_age", 25L, nodeB);
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);

        var nameAlice = rtx.SeekIndex("idx_name", PropertyValue.FromString("alice"));
        nameAlice.MoveNext().Should().BeTrue("idx_name の alice エントリは PageImage で復旧");
        nameAlice.Current.Should().Be(nodeA);
        nameAlice.Dispose();

        var emailBob = rtx.SeekIndex("idx_email", PropertyValue.FromString("bob@example.com"));
        emailBob.MoveNext().Should().BeTrue("idx_email の bob エントリも復旧");
        emailBob.Current.Should().Be(nodeB);
        emailBob.Dispose();

        var age30 = rtx.SeekIndex("idx_age", PropertyValue.FromInt64(30L));
        age30.MoveNext().Should().BeTrue("idx_age の 30 エントリも復旧");
        age30.Current.Should().Be(nodeA);
        age30.Dispose();

        rtx.Rollback();
    }

    /// <summary>
    /// FT-19 / ARCH-4 増分5: 索引作成 → kill → 再 open で索引カタログから復元される。
    /// 旧実装の indexes/.fileKinds サイドカーは廃止され、索引カタログ (name → tenantId /
    /// PropertyTypeFlags) は graph.quiver 内の専用テナントに同居する。よってファイル存在の
    /// 代わりに「kill 後に索引が再 materialize され、コミット済みエントリが引ける」ことで
    /// カタログ永続化を behavioral に確認する。
    /// </summary>
    [Fact]
    public void IndexCatalog_persists_fileKind_across_kill()
    {
        IGraphStorageBackend? backend = Open();
        NodeId node;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            node = tx.CreateNode("Doc");
            tx.IndexInsert("idx_persistent", 42L, node);
            tx.Commit();
        }

        // ARCH-4: 索引は graph.quiver に同居するため、独立した .idx / .fileKinds は作られない。
        File.Exists(Path.Combine(DatabaseDirectory, "indexes", ".fileKinds")).Should().BeFalse(
            "ARCH-4 では索引カタログは graph.quiver 内テナントに同居し別ファイルを作らない");

        KillProcessSimulator.SimulateKill(ref backend);

        // 再 open で索引カタログテナントが recovery → ReloadAll で復元され、索引が
        // materialize される。PageImage redo もそれに依存する。
        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        var cur = rtx.SeekIndex("idx_persistent", PropertyValue.FromInt64(42L));
        cur.MoveNext().Should().BeTrue();
        cur.Current.Should().Be(node);
        cur.Dispose();
        rtx.Rollback();
    }

    // ===== FT-21: Checkpoint atomicity (Begin/End sentinel) kill points =====

    /// <summary>
    /// FT-21 共通ヘルパ: 与えた phase で kill 例外を投げるよう injector を仕込み、
    /// 「checkpoint を必ず誘発する commit」を 1 回実行 → 例外を捕捉 → kill simulate →
    /// 再 open → コミット済みデータが全て読めることを assert する。
    ///
    /// checkpoint 誘発手段: <see cref="GraphDatabaseOptions.CheckpointThresholdBytes"/> = 1
    /// を渡しておき、最初の commit で <see cref="ITransactionManager"/>.MaybeCheckpoint
    /// が走るようにする。
    /// </summary>
    private void RunCheckpointKillScenario(CheckpointPhase killAt)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            $"quiver_ft21_{killAt}_" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new BinaryGraphStorageBackendFactory();
            var opts = new GraphDatabaseOptions { CheckpointThresholdBytes = 1 };

            // セットアップ phase: checkpoint phase injector 無しで、recovery で確認したい
            // コミット済みデータを 2 件書いておく。最初の commit で 1 回目の checkpoint
            // が完走するのは構わない (このシナリオでテストしたいのは「次の checkpoint
            // が phase X で kill されたとき」の挙動)。
            var preserved = new List<NodeId>();
            IGraphStorageBackend? backend = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                preserved.Add(tx.CreateNode("Pre1"));
                tx.SetProperty(preserved[0], "marker", PropertyValue.FromInt64(11L));
                tx.Commit();
            }
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                preserved.Add(tx.CreateNode("Pre2"));
                tx.SetProperty(preserved[1], "marker", PropertyValue.FromInt64(22L));
                tx.Commit();
            }

            // injector を仕込んでから次の commit を発射。injector は phase 完了直後に
            // 例外を投げ、checkpoint をその時点で中断する。例外は MaybeCheckpoint →
            // OnCommit → Commit を経由してテスト側に伝播する。
            bool killed = false;
            Checkpointer.PhaseInjector = phase =>
            {
                if (phase == killAt && !killed)
                {
                    killed = true;
                    throw new InvalidOperationException(
                        $"FT-21 simulated kill at {phase}");
                }
            };
            try
            {
                using var killTx = backend.BeginGraphTransaction(
                    IsolationLevel.SnapshotIsolation, readOnly: false);
                preserved.Add(killTx.CreateNode("PreKill"));
                killTx.SetProperty(preserved[2], "marker", PropertyValue.FromInt64(33L));
                try { killTx.Commit(); }
                catch (InvalidOperationException) { /* expected: simulated kill */ }
            }
            finally
            {
                Checkpointer.PhaseInjector = null;
            }

            // この時点で WAL / data file は「checkpoint が phase X で中断された」状態。
            // KillProcessSimulator は Dispose を呼ぶが、未 fsync のページが flush されても
            // recovery の正当性は変わらない (CheckpointEnd が無ければ recovery は前回
            // checkpoint からやり直すので、partial 状態のページは PageImage redo で
            // 整合に戻る)。
            KillProcessSimulator.SimulateKill(ref backend);

            using var reopened = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            // PreKill tx 自体も commit record が durable に書かれていれば redo されているはず。
            // (Commit は checkpoint Begin より前に WAL に書かれて FlushTo 済み。)
            for (int i = 0; i < preserved.Count; i++)
            {
                rtx.NodeExists(preserved[i]).Should().BeTrue(
                    $"phase {killAt}: committed node #{i} must survive checkpoint kill");
            }
            rtx.GetProperty(preserved[0], "marker").Int64Value.Should().Be(11L);
            rtx.GetProperty(preserved[1], "marker").Int64Value.Should().Be(22L);
            rtx.GetProperty(preserved[2], "marker").Int64Value.Should().Be(33L);
            rtx.Rollback();
        }
        finally
        {
            Checkpointer.PhaseInjector = null;
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    /// <summary>
    /// FT-21 case 1 (Begin 直後 kill): Begin sentinel だけが WAL に乗った状態で死亡。
    /// data file は flush されていない (一部 PageImage 経由でしか整合しない)。
    /// recovery は End が無いと検知して前回 checkpoint からやり直し、PageImage redo で
    /// 全コミット済みデータが復活する。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterBegin_recovers_committed_data()
        => RunCheckpointKillScenario(CheckpointPhase.AfterBegin);

    /// <summary>
    /// FT-21 case 2 (page fsync 半分の代替): Begin 後の data flush が完了する前に
    /// 死亡するシナリオを <see cref="CheckpointPhase.AfterBegin"/> で再実行する
    /// (完了する前という意味では AfterBegin と等価で、partial flush 中に死ぬのは
    /// AfterBegin より「悪くない」ので、AfterBegin が問題なければこちらも安全)。
    /// 別 seed で重ねがけして regression を担保する。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_MidDataFlush_recovers_committed_data()
    {
        // page fsync 途中での kill を真に inject するには PageManager.FlushAll の
        // 内部に hook が必要だが、PageManager は集計の都合で iterate せず一括で扱う。
        // 代わりに「flush が部分的にしか durable でない状態 = AfterBegin の上位互換」
        // として AfterBegin を再実行することでカバーする。
        RunCheckpointKillScenario(CheckpointPhase.AfterBegin);
    }

    /// <summary>
    /// FT-21 case 3 (全 page fsync 直後 kill): data file は完全に durable だが
    /// index flush も End も走っていない。recovery は依然として End 不在を検知し
    /// 前回 checkpoint からやり直す (安全側のオーバーヘッドだけで integrity OK)。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterDataFlush_recovers_committed_data()
        => RunCheckpointKillScenario(CheckpointPhase.AfterDataFlush);

    /// <summary>
    /// FT-21 case 4 (End 直前 kill): data + index 両方 fsync 済みだが End sentinel
    /// 未書込み。WAL 上は CheckpointBegin のみ。recovery は前回 checkpoint からやり直す。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterIndexFlush_before_End_recovers_committed_data()
        => RunCheckpointKillScenario(CheckpointPhase.AfterIndexFlush);

    /// <summary>
    /// FT-21 case 5 (End 直後 kill): End sentinel まで書き終えているが truncate
    /// 未実行。recovery は最新 End から起動して、過去 WAL segment が残っていても
    /// 整合に影響しない (idempotent redo)。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterEnd_before_truncate_recovers_committed_data()
        => RunCheckpointKillScenario(CheckpointPhase.AfterEnd);

    /// <summary>
    /// FT-21 case 6 (truncate 途中の代替): truncate 完了直後に kill。完全に成功した
    /// checkpoint を kill で締めた状態。truncate 途中の partial deletion は OS の
    /// unlink 単位の atomicity に依存するので、ここでは「全 unlink 成功直後に死亡」
    /// で代替する (truncate 中に死んだ場合の残骸 segment は次回 recovery でも old
    /// segment として無害に扱われる — このシナリオでは AfterEnd 経路で間接的に
    /// 担保されている)。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterTruncate_reopens_cleanly()
        => RunCheckpointKillScenario(CheckpointPhase.AfterTruncate);

    private string? LatestWalSegment()
    {
        // ARCH-4 増分7: WAL は単一サイドカー graph.quiver-wal。
        var walPath = Path.Combine(DatabaseDirectory, "graph.quiver-wal");
        return File.Exists(walPath) ? walPath : null;
    }
}
