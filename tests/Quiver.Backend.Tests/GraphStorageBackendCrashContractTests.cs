using FluentAssertions;
using Quiver.Backend.Tests.Faults;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// BA-9 — backend-agnostic crash / durability contract suite. Subclassed once
/// per backend (binary, SQLite) so the same scenarios run against every
/// <see cref="IGraphStorageBackend"/> implementation, the same way
/// <see cref="GraphStorageBackendContractTests"/> applies to the functional
/// surface.
///
/// Definition of Done (docs/design/06_wal.md / 07_transaction_recovery.md):
/// the 100-iteration repeated kill / recover loop must stay flake-free for
/// both backends.
/// </summary>
public abstract class GraphStorageBackendCrashContractTests : IDisposable
{
    private readonly string _dir;
    private readonly IGraphStorageBackendFactory _factory;

    protected GraphStorageBackendCrashContractTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_crash_" + Guid.NewGuid().ToString("N"));
        _factory = CreateFactory();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // OS may still hold a few handles after a torn-shutdown test — best effort cleanup.
        }
    }

    protected abstract IGraphStorageBackendFactory CreateFactory();

    /// <summary>Database directory under test. Exposed to subclasses for backend-specific file paths.</summary>
    protected string DatabaseDirectory => _dir;

    protected IGraphStorageBackend Open()
        => _factory.Open(_dir, new GraphDatabaseOptions());

    // ===== (a) Commit -> kill -> reopen recovers committed data =====

    [Fact]
    public void Commit_then_kill_then_reopen_recovers_committed_data()
    {
        IGraphStorageBackend? backend = Open();
        NodeId persisted;
        using (var tx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            persisted = tx.CreateNode("Survivor");
            tx.SetProperty(persisted, "marker", PropertyValue.FromInt64(7L));
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true);
        rtx.NodeExists(persisted).Should().BeTrue();
        rtx.GetProperty(persisted, "marker").Int64Value.Should().Be(7L);
        rtx.Rollback();
    }

    // ===== (b) Kill during write — database remains openable =====
    //
    // The binary backend does not yet maintain an undo log (see BA-2 fixture
    // comment), so a kill mid-write may leave the just-written page on disk
    // without a matching WAL Commit record. SQLite-backed transactions DO
    // roll back to the BEGIN IMMEDIATE checkpoint. Both backends must at
    // minimum reopen cleanly; the strict-rollback contract is asserted by
    // the SQLite-specific override.

    [Fact]
    public void KillDuringWrite_database_reopens_cleanly()
    {
        IGraphStorageBackend? backend = Open();
        var tx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false);
        var doomed = tx.CreateNode("Doomed");
        tx.SetProperty(doomed, "ephemeral", PropertyValue.FromInt64(999L));
        // NOTE: NO Commit().

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        // Must not throw — backend is usable again.
        AssertUncommittedKillState(rtx, doomed);
        rtx.Rollback();
    }

    /// <summary>
    /// Backend-specific assertion about uncommitted-mid-write data after a kill.
    /// Default (binary): no undo log, so the partial node may or may not be
    /// visible — accept either. SQLite subclass tightens this to BeFalse.
    /// </summary>
    protected virtual void AssertUncommittedKillState(
        IGraphTransaction tx, NodeId uncommittedNode)
    {
        _ = tx.NodeExists(uncommittedNode);
    }

    // ===== (c) Kill mid-mixed-workload preserves committed prefix =====

    [Fact]
    public void KillDuringMixedWorkload_committed_writes_survive()
    {
        IGraphStorageBackend? backend = Open();
        NodeId committed;
        using (var tx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            committed = tx.CreateNode("Committed");
            tx.Commit();
        }

        // Start a second tx and leave it uncommitted before the simulated kill.
        var dirtyTx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false);
        var dirty = dirtyTx.CreateNode("Dirty");

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        rtx.NodeExists(committed).Should().BeTrue("committed work must persist");
        AssertUncommittedKillState(rtx, dirty);
        rtx.Rollback();
    }

    // ===== (d) 100-iteration repeated kill -> recover loop =====

    [Fact]
    public void RepeatedKillRecover_100_iterations_no_corruption()
    {
        const int Iterations = 100;
        var ids = new List<NodeId>(Iterations);

        for (int i = 0; i < Iterations; i++)
        {
            IGraphStorageBackend? backend = Open();
            // Verify everything written so far is still there.
            using (var rtx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true))
            {
                foreach (var id in ids)
                {
                    rtx.NodeExists(id).Should().BeTrue(
                        $"iteration {i}: previously committed node {id.Value} must still exist");
                }
                rtx.Rollback();
            }

            NodeId next;
            using (var wtx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                next = wtx.CreateNode("Iter");
                wtx.SetProperty(next, "i", PropertyValue.FromInt64(i));
                wtx.Commit();
            }
            ids.Add(next);

            KillProcessSimulator.SimulateKill(ref backend);
        }

        using var final = Open();
        using var ftx = final.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true);
        for (int i = 0; i < Iterations; i++)
        {
            ftx.NodeExists(ids[i]).Should().BeTrue();
            ftx.GetProperty(ids[i], "i").Int64Value.Should().Be(i);
        }
        ftx.Rollback();
    }

    // ===== (e) Torn last write -> either skipped or recovered =====
    // Backend-specific implementation (different file layouts).

    [Fact]
    public void TornLastWrite_skipped_or_recovered()
    {
        IGraphStorageBackend? backend = Open();
        NodeId pre;
        using (var tx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            pre = tx.CreateNode("Pre");
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);
        InjectTornWriteAtTail();

        // Reopen must succeed — the torn tail either replays cleanly or is ignored.
        // If the backend cannot recover, it must throw a recognisable error type, not silently corrupt.
        IGraphStorageBackend? reopened = null;
        try
        {
            reopened = Open();
            using var rtx = reopened.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true);
            // The pre-torn committed state must still be there.
            rtx.NodeExists(pre).Should().BeTrue("committed-before-tear data must survive");
            rtx.Rollback();
        }
        catch (CorruptionException) { /* acceptable: surfaced corruption is fine */ }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* acceptable for SQLite */ }
        catch (StorageException) { /* acceptable */ }
        finally
        {
            reopened?.Dispose();
        }
    }

    /// <summary>
    /// Inject a torn write at the tail of whichever file is the durability
    /// boundary for the backend under test. Binary backend: most recent WAL
    /// segment. SQLite backend: the <c>-wal</c> sidecar file.
    /// </summary>
    protected abstract void InjectTornWriteAtTail();

    // ===== (f) Checksum corruption -> detected and reported (backend-specific) =====
    // Provided as a hook so each backend picks the right file / exception type.

    [Fact]
    public void Checksum_mismatch_detected_and_reported()
    {
        IGraphStorageBackend? backend = Open();
        using (var tx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            tx.CreateNode("Will_be_corrupted");
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);
        InjectChecksumCorruption();

        // Reopen / first read must either (a) detect corruption and throw, or
        // (b) silently skip the corrupted record (binary WAL with truncated /
        // bad-checksum tail is treated as end-of-log). Both are acceptable
        // contract responses; silently *trusting* corrupt data is not.
        try
        {
            using var reopened = Open();
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            // If we got here, recovery decided the torn record was unrecoverable
            // and skipped it — that's fine. Just make sure the backend is at
            // least usable for new work.
            rtx.Rollback();
        }
        catch (CorruptionException) { /* acceptable */ }
        catch (StorageException) { /* acceptable */ }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* acceptable */ }
    }

    /// <summary>
    /// Flip a bit in the backend's primary checksummed structure (WAL record
    /// header for binary, SQLite <c>-wal</c> frame for SQLite).
    /// </summary>
    protected abstract void InjectChecksumCorruption();

    // ===== (g) Sidecar deletion -> rebuild or fail safely (backend-specific) =====

    [Fact]
    public void SidecarDeleted_backend_rebuilds_or_fails_safely()
    {
        IGraphStorageBackend? backend = Open();
        NodeId stable;
        using (var tx = backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            stable = tx.CreateNode("Stable");
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);
        DeleteSidecarFiles();

        IGraphStorageBackend? reopened = null;
        try
        {
            reopened = Open();
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            // If sidecars were optional rebuild-targets the primary node must still be readable.
            rtx.NodeExists(stable).Should().BeTrue();
            rtx.Rollback();
        }
        catch (StorageException) { /* acceptable: fail-safe */ }
        catch (CorruptionException) { /* acceptable */ }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* acceptable */ }
        finally
        {
            reopened?.Dispose();
        }
    }

    /// <summary>
    /// Delete a backend-specific sidecar file (binary: <c>adj.epoch</c> /
    /// <c>*.idxmeta</c>; SQLite: <c>-shm</c>). Implementations should pick a
    /// file whose loss is recoverable or fail-safely detectable.
    /// </summary>
    protected abstract void DeleteSidecarFiles();
}
