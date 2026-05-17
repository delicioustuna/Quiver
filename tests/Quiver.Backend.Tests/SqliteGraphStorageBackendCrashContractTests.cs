using FluentAssertions;
using Microsoft.Data.Sqlite;
using Quiver.Backend.Tests.Faults;
using Quiver.Core;
using Quiver.Storage.Sqlite;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// BA-9 SQLite backend crash contract: runs the shared
/// <see cref="GraphStorageBackendCrashContractTests"/> suite plus three
/// SQLite-specific scenarios (PRAGMA round-trip, <c>-wal</c>/<c>-shm</c>
/// sidecar loss, InMemoryVectorStore non-persistence).
/// </summary>
public sealed class SqliteGraphStorageBackendCrashContractTests
    : GraphStorageBackendCrashContractTests
{
    protected override IGraphStorageBackendFactory CreateFactory()
        => new SqliteGraphStorageBackendFactory();

    /// <summary>
    /// SQLite enforces atomic transactions via BEGIN IMMEDIATE, so an
    /// uncommitted writer's work must NOT be visible after a kill.
    /// </summary>
    protected override void AssertUncommittedKillState(
        IGraphTransaction tx, NodeId uncommittedNode)
        => tx.NodeExists(uncommittedNode).Should().BeFalse(
            "SQLite is fully atomic; uncommitted writes must vanish after a kill");

    protected override void InjectTornWriteAtTail()
    {
        // SQLite's durability boundary in WAL mode is the -wal sidecar. A torn
        // tail in -wal should be treated by SQLite as truncated frames and
        // dropped on next open.
        var walPath = Path.Combine(DatabaseDirectory, "quiver.sqlite-wal");
        if (File.Exists(walPath))
            TornWriteInjector.ZeroFillTail(walPath, tailBytes: 16);
    }

    protected override void InjectChecksumCorruption()
    {
        // Flip a bit inside the -wal sidecar frame. SQLite frame checksums
        // will detect this on the next read. If the WAL has been checkpointed
        // away (small DB) there's nothing to corrupt — that's fine, recovery
        // simply succeeds.
        var walPath = Path.Combine(DatabaseDirectory, "quiver.sqlite-wal");
        if (File.Exists(walPath) && new FileInfo(walPath).Length > 64)
        {
            // 32 = inside the first frame header, past the WAL file header.
            ChecksumCorruptor.FlipBitAt(walPath, offset: 32, bitInByte: 3);
        }
    }

    protected override void DeleteSidecarFiles()
    {
        // The -shm file is purely an mmap'd index over -wal. SQLite rebuilds
        // it on next open.
        var shmPath = Path.Combine(DatabaseDirectory, "quiver.sqlite-shm");
        SidecarFileDeleter.TryDelete(shmPath);
    }

    // ===== SQLite-specific scenarios =====

    /// <summary>
    /// After a kill + reopen, the connection must still come up with the
    /// expected PRAGMA values. Regression guard: if the factory ever stops
    /// setting <c>journal_mode=WAL</c> or <c>synchronous=NORMAL</c>, crash
    /// recovery semantics degrade silently — fail loudly here instead.
    /// </summary>
    [Fact]
    public void PragmaValues_intact_after_kill_and_reopen()
    {
        IGraphStorageBackend? backend = Open();
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            tx.CreateNode("Probe");
            tx.Commit();
        }
        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = SqliteGraphStorageBackendFactory.OpenBackend(
            DatabaseDirectory, new GraphDatabaseOptions());

        string journalMode = ReadPragma(reopened.Connection, "journal_mode");
        journalMode.Should().BeEquivalentTo("wal",
            "WAL mode is required for the crash recovery contract");

        string synchronous = ReadPragma(reopened.Connection, "synchronous");
        // synchronous=NORMAL surfaces as 1.
        synchronous.Should().BeOneOf("1", "NORMAL");
    }

    /// <summary>
    /// Delete both -wal and -shm sidecars after committing data. SQLite must
    /// still open the database and the committed data must remain readable
    /// (anything still in -wal at the moment of deletion is genuinely lost —
    /// SQLite has no other source for it).
    /// </summary>
    [Fact]
    public void WalShmSidecars_deleted_committed_data_after_checkpoint_survives()
    {
        IGraphStorageBackend? backend = Open();
        NodeId persisted;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            persisted = tx.CreateNode("DurableAcrossSidecarLoss");
            tx.Commit();
        }

        // Force a checkpoint so the committed work lands in the main DB file,
        // not the -wal sidecar (only then is it truly safe to delete -wal).
        if (backend is SqliteGraphStorageBackend concrete)
        {
            using var cmd = concrete.Connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }

        KillProcessSimulator.SimulateKill(ref backend);

        SidecarFileDeleter.TryDelete(Path.Combine(DatabaseDirectory, "quiver.sqlite-wal"));
        SidecarFileDeleter.TryDelete(Path.Combine(DatabaseDirectory, "quiver.sqlite-shm"));

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        rtx.NodeExists(persisted).Should().BeTrue(
            "post-checkpoint data sits in the main DB file and must survive sidecar loss");
        rtx.Rollback();
    }

    /// <summary>
    /// <see cref="InMemoryVectorStore"/> is currently the only IVectorStore the
    /// SQLite backend offers (BA-5 / VEC-1). Vectors written before a crash
    /// must NOT survive — record that explicitly so a future persistent
    /// vector backend doesn't quietly change the contract.
    /// </summary>
    [Fact]
    public void InMemoryVectorStore_is_non_persistent_across_kill()
    {
        const string IndexName = "vec_idx";
        IGraphStorageBackend? backend = Open();
        NodeId target;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            target = tx.CreateNode("Vec");
            tx.Commit();
        }

        backend!.Vectors.CreateVectorIndex(new VectorIndexSpec(
            Name: IndexName,
            EntityKind: EntityKind.Node,
            SourcePropertyKeyId: new PropertyKeyId(0),
            Dimensions: 4,
            Metric: DistanceMetric.Cosine,
            ProviderId: "test"));
        ReadOnlySpan<float> v = stackalloc float[] { 1f, 0f, 0f, 0f };
        backend.Vectors.SetVector(EntityKind.Node, target.Value, IndexName, v);

        KillProcessSimulator.SimulateKill(ref backend);

        // After kill the in-memory index is gone. KnnSearch must fail (or
        // return no hits matching the persisted node) — never silently
        // resurrect the lost vector. The current InMemoryVectorStore raises
        // VectorException for unknown indexes.
        using var reopened = Open();
        var queryArr = new float[] { 1f, 0f, 0f, 0f };
        try
        {
            using var cursor = reopened.Vectors.KnnSearch(IndexName, queryArr, k: 1);
            // If a future persistent VectorStore lands this assert will fail
            // and someone has to come update the contract test.
            cursor.MoveNext().Should().BeFalse(
                "InMemoryVectorStore is non-persistent; vectors must not survive a kill.");
        }
        catch (VectorException) { /* acceptable: index itself is gone */ }
    }

    private static string ReadPragma(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {name};";
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "";
    }
}
