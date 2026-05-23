using FluentAssertions;
using Quiver.Backend.Tests.Faults;
using Quiver.Core;
using Quiver.Stores;
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
                IGraphStorageBackend? backend = factory.Open(dir, opts);
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

            using var reopened = factory.Open(dir, opts);
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            foreach (var id in ids)
                rtx.NodeExists(id).Should().BeTrue();
            rtx.Rollback();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
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
                .Open(dir, new GraphDatabaseOptions());
            using var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false);
            var node = tx.CreateNode("First");
            tx.Commit();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
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
            IGraphStorageBackend? backend = factory.Open(dir, opts);
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

            using var reopened = factory.Open(dir, opts);
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
            try { Directory.Delete(dir, recursive: true); } catch { }
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
    /// FT-19: 索引作成 → kill → 再 open で fileKind catalog から復元される。
    /// catalog ファイル (indexes/.fileKinds) が永続化されていることを backend レベルで確認。
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

        // catalog ファイルが存在することを確認
        var catalogPath = Path.Combine(DatabaseDirectory, "indexes", ".fileKinds");
        File.Exists(catalogPath).Should().BeTrue("catalog file は CreateIndex 時に永続化される");
        var catalogContent = File.ReadAllText(catalogPath);
        catalogContent.Should().Contain("idx_persistent\t",
            "catalog に索引名と fileKind が記録されている");

        KillProcessSimulator.SimulateKill(ref backend);

        // 再 open で catalog から fileKind が復元され、索引が WAL ロギング対象として
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

    private string? LatestWalSegment()
    {
        var walDir = Path.Combine(DatabaseDirectory, "wal");
        if (!Directory.Exists(walDir)) return null;
        var segs = Directory.EnumerateFiles(walDir)
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .ToList();
        return segs.Count == 0 ? null : segs[0];
    }
}
