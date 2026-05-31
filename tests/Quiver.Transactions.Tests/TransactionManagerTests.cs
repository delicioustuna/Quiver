using FluentAssertions;
using Quiver.Core;
using Quiver.Index;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Transactions.Tests;

public class TransactionManagerTests : IDisposable
{
    private readonly string _walDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly WriteAheadLog _wal;
    private readonly TransactionManager _manager;

    public TransactionManagerTests()
    {
        _wal = new WriteAheadLog(_walDir);
        _manager = new TransactionManager(_wal,
            new StubNodeStore(), new StubRelationshipStore(),
            new StubPropertyStore(), new NullIndexManager());
    }

    public void Dispose()
    {
        _manager.Dispose();
        _wal.Dispose();
        if (Directory.Exists(_walDir)) Directory.Delete(_walDir, recursive: true);
    }

    [Fact]
    public void Begin_returns_active_transaction()
    {
        using var tx = _manager.Begin();
        tx.State.Should().Be(TransactionState.Active);
        tx.Id.IsValid.Should().BeTrue();
        _manager.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void Commit_changes_state_and_removes_from_active()
    {
        var tx = _manager.Begin();
        tx.Commit();
        tx.State.Should().Be(TransactionState.Committed);
        _manager.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Abort_changes_state_and_removes_from_active()
    {
        var tx = _manager.Begin();
        tx.Abort();
        tx.State.Should().Be(TransactionState.Aborted);
        _manager.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_transaction_aborts_if_still_active()
    {
        var tx = _manager.Begin();
        tx.Dispose();
        tx.State.Should().Be(TransactionState.Aborted);
        _manager.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Commit_on_already_committed_throws()
    {
        var tx = _manager.Begin();
        tx.Commit();
        var act = () => tx.Commit();
        act.Should().Throw<TransactionException>();
    }

    [Fact]
    public void Abort_on_committed_is_noop()
    {
        var tx = _manager.Begin();
        tx.Commit();
        var act = () => tx.Abort();
        act.Should().NotThrow();
        tx.State.Should().Be(TransactionState.Committed);
    }

    [Fact]
    public void Multiple_transactions_tracked_independently()
    {
        var tx1 = _manager.Begin();
        var tx2 = _manager.Begin();
        _manager.ActiveCount.Should().Be(2);

        tx1.Commit();
        _manager.ActiveCount.Should().Be(1);

        tx2.Abort();
        _manager.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Snapshot_lsn_is_set_at_begin_time()
    {
        using var tx = _manager.Begin();
        tx.SnapshotLsn.Should().BeGreaterThanOrEqualTo(-1);
    }

    [Fact]
    public void Oldest_active_lsn_equals_flushed_lsn_when_no_active_transactions()
    {
        _manager.OldestActiveLsn.Should().Be(_wal.FlushedLsn);
    }

    [Fact]
    public void Oldest_active_lsn_tracks_earliest_snapshot()
    {
        using var tx1 = _manager.Begin();
        using var tx2 = _manager.Begin();
        _manager.OldestActiveLsn.Should().Be(Math.Min(tx1.SnapshotLsn, tx2.SnapshotLsn));
    }

    [Fact]
    public void Transaction_ids_are_unique()
    {
        using var tx1 = _manager.Begin();
        using var tx2 = _manager.Begin();
        tx1.Id.Should().NotBe(tx2.Id);
    }

    [Fact]
    public void Isolation_level_is_preserved()
    {
        using var tx = _manager.Begin(IsolationLevel.ReadCommitted);
        tx.Level.Should().Be(IsolationLevel.ReadCommitted);
    }

    // ---- Commit hook tests (VEC-3) ----

    [Fact]
    public void OnCommitted_fires_after_commit_in_registration_order()
    {
        var tx = _manager.Begin();
        var order = new List<int>();
        tx.OnCommitted(() => order.Add(1));
        tx.OnCommitted(() => order.Add(2));
        tx.OnCommitted(() => order.Add(3));

        order.Should().BeEmpty();
        tx.Commit();
        order.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void OnRolledBack_fires_on_abort_in_registration_order()
    {
        var tx = _manager.Begin();
        var order = new List<int>();
        tx.OnRolledBack(() => order.Add(1));
        tx.OnRolledBack(() => order.Add(2));

        tx.Abort();
        order.Should().Equal(1, 2);
    }

    [Fact]
    public void OnRolledBack_does_not_fire_on_commit()
    {
        var tx = _manager.Begin();
        bool fired = false;
        tx.OnRolledBack(() => fired = true);
        tx.Commit();
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnCommitted_does_not_fire_on_abort()
    {
        var tx = _manager.Begin();
        bool fired = false;
        tx.OnCommitted(() => fired = true);
        tx.Abort();
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnRolledBack_fires_when_disposing_active_transaction()
    {
        var tx = _manager.Begin();
        bool fired = false;
        tx.OnRolledBack(() => fired = true);
        tx.Dispose();
        fired.Should().BeTrue();
    }

    [Fact]
    public void Hook_exception_does_not_break_transaction_or_other_hooks()
    {
        var tx = _manager.Begin();
        bool secondRan = false;
        tx.OnCommitted(() => throw new InvalidOperationException("boom"));
        tx.OnCommitted(() => secondRan = true);

        var act = () => tx.Commit();
        act.Should().NotThrow();
        secondRan.Should().BeTrue();
        tx.State.Should().Be(TransactionState.Committed);
    }

    [Fact]
    public void OnCommitted_registered_after_commit_fires_immediately()
    {
        var tx = _manager.Begin();
        tx.Commit();
        bool fired = false;
        tx.OnCommitted(() => fired = true);
        fired.Should().BeTrue();
    }

    [Fact]
    public void OnRolledBack_registered_after_commit_is_ignored()
    {
        var tx = _manager.Begin();
        tx.Commit();
        bool fired = false;
        tx.OnRolledBack(() => fired = true);
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnRolledBack_registered_after_abort_fires_immediately()
    {
        var tx = _manager.Begin();
        tx.Abort();
        bool fired = false;
        tx.OnRolledBack(() => fired = true);
        fired.Should().BeTrue();
    }

    // ---- Lock manager tests ----

    [Fact]
    public void Lock_acquired_twice_by_same_tx_succeeds()
    {
        var lm = new LockManager();
        var txId = new TransactionId(1);
        lm.TryAcquire(42L, txId).Should().BeTrue();
        lm.TryAcquire(42L, txId).Should().BeTrue(); // re-entrant
    }

    [Fact]
    public void Lock_released_allows_reacquisition_by_other_tx()
    {
        var lm = new LockManager();
        var tx1 = new TransactionId(1);
        var tx2 = new TransactionId(2);

        lm.TryAcquire(10L, tx1).Should().BeTrue();
        lm.Release(10L, tx1);
        lm.TryAcquire(10L, tx2).Should().BeTrue();
    }

    [Fact]
    public void ReleaseAll_removes_all_locks_of_transaction()
    {
        var lm = new LockManager();
        var txId = new TransactionId(5);

        lm.TryAcquire(1L, txId);
        lm.TryAcquire(2L, txId);
        lm.TryAcquire(3L, txId);
        lm.ReleaseAll(txId);

        var other = new TransactionId(6);
        lm.TryAcquire(1L, other).Should().BeTrue();
        lm.TryAcquire(2L, other).Should().BeTrue();
        lm.TryAcquire(3L, other).Should().BeTrue();
    }

    // ---- Stubs ----

    private sealed class StubNodeStore : INodeStore
    {
        public long InUseCount => 0;
        public NodeId Allocate(LabelId labelId) => NodeId.Invalid;
        public void Free(NodeId nodeId) { }
        public NodeReadHandle Read(NodeId nodeId) => throw new NotSupportedException();
        public NodeWriteHandle Write(NodeId nodeId) => throw new NotSupportedException();
        public IEnumerable<NodeId> Scan() => [];
    }

    private sealed class StubRelationshipStore : IRelationshipStore
    {
        public long InUseCount => 0;
        public RelationshipId Create(INodeStore ns, NodeId src, NodeId tgt, RelationshipTypeId type) => RelationshipId.Invalid;
        public void Delete(INodeStore ns, RelationshipId relId) { }
        public RelationshipReadHandle Read(RelationshipId relId) => throw new NotSupportedException();
        public RelationshipWriteHandle Write(RelationshipId relId) => throw new NotSupportedException();
        public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore ns) => throw new NotSupportedException();
        public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore ns, RelationshipTypeId type, Direction dir) => throw new NotSupportedException();
        public IEnumerable<RelationshipId> Scan() => [];
    }

    private sealed class StubPropertyStore : IPropertyStore
    {
        public PropertyId Create(PropertyKeyId keyId, in PropertyValue value, PropertyId currentFirst) => PropertyId.Invalid;
        public PropertyId Delete(PropertyId propId, PropertyId currentFirst) => PropertyId.Invalid;
        public PropertyReadHandle Read(PropertyId propId) => throw new NotSupportedException();
        public PropertyEnumerator Enumerate(PropertyId firstPropId) => throw new NotSupportedException();
    }

    private sealed class NullIndexManager : IIndexManager
    {
        public IBTreeIndex<int> CreateInt32Index(string name) => throw new NotSupportedException();
        public IBTreeIndex<long> CreateInt64Index(string name) => throw new NotSupportedException();
        public IBTreeIndex<double> CreateDoubleIndex(string name) => throw new NotSupportedException();
        public IBTreeIndex<string> CreateStringIndex(string name) => throw new NotSupportedException();
        public IBTreeIndex<byte[]> CreateBytesIndex(string name) => throw new NotSupportedException();
        public bool DropIndex(string name) => false;
        public IEnumerable<string> ListIndexes() => [];
    }
}
