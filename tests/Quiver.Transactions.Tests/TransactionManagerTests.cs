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
        _wal = new WriteAheadLog(Path.Combine(_walDir, "wal"));
        _manager = new TransactionManager(_wal,
            new StubVertexStore(), new StubEdgeStore(),
            new StubPropertyStore(), new NullIndexManager());
    }

    public void Dispose()
    {
        _manager.Dispose();
        _wal.Dispose();
        if (Directory.Exists(_walDir)) Directory.Delete(_walDir, recursive: true);
    }

    [Fact]
    public void BeginWrite_returns_active_transaction()
    {
        using var tx = _manager.BeginWrite();
        tx.State.Should().Be(TransactionState.Active);
        tx.Id.IsValid.Should().BeTrue();
        _manager.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void Commit_changes_state_and_removes_from_active()
    {
        var tx = _manager.BeginWrite();
        tx.Commit();
        tx.State.Should().Be(TransactionState.Committed);
        _manager.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Abort_changes_state_and_removes_from_active()
    {
        var tx = _manager.BeginWrite();
        tx.Abort();
        tx.State.Should().Be(TransactionState.Aborted);
        _manager.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_transaction_aborts_if_still_active()
    {
        var tx = _manager.BeginWrite();
        tx.Dispose();
        tx.State.Should().Be(TransactionState.Aborted);
        _manager.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Commit_on_already_committed_throws()
    {
        var tx = _manager.BeginWrite();
        tx.Commit();
        var act = () => tx.Commit();
        act.Should().Throw<TransactionException>();
    }

    [Fact]
    public void Abort_on_committed_is_noop()
    {
        var tx = _manager.BeginWrite();
        tx.Commit();
        var act = () => tx.Abort();
        act.Should().NotThrow();
        tx.State.Should().Be(TransactionState.Committed);
    }

    [Fact]
    public void Multiple_read_transactions_are_tracked_independently()
    {
        var tx1 = _manager.BeginRead();
        var tx2 = _manager.BeginRead();
        _manager.ActiveCount.Should().Be(2);

        tx1.Commit();
        _manager.ActiveCount.Should().Be(1);

        tx2.Abort();
        _manager.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Snapshot_lsn_is_set_at_begin_time()
    {
        using var tx = _manager.BeginRead();
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
        using var tx1 = _manager.BeginRead();
        using var tx2 = _manager.BeginRead();
        _manager.OldestActiveLsn.Should().Be(Math.Min(tx1.SnapshotLsn, tx2.SnapshotLsn));
    }

    [Fact]
    public void Transaction_ids_are_unique()
    {
        using var tx1 = _manager.BeginRead();
        using var tx2 = _manager.BeginRead();
        tx1.Id.Should().NotBe(tx2.Id);
    }

    [Fact]
    public void Isolation_level_is_preserved()
    {
        using var tx = _manager.BeginWrite(IsolationLevel.ReadCommitted);
        tx.Level.Should().Be(IsolationLevel.ReadCommitted);
    }

    [Fact]
    public void Read_transaction_writes_no_wal_bytes()
    {
        long before = _wal.BytesWritten;
        using (var tx = _manager.BeginRead())
            tx.Commit();
        _wal.BytesWritten.Should().Be(before);
    }

    [Fact]
    public void Durable_publish_failure_faults_manager_and_rejects_new_operations()
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        using var wal = new WriteAheadLog(Path.Combine(directory, "wal"));
        using var manager = new TransactionManager(
            wal,
            new StubVertexStore(),
            new StubEdgeStore(),
            new StubPropertyStore(),
            new NullIndexManager(),
            nexusStore: new StubNexusStore(),
            coMembershipStore: new ThrowingCoMembershipStore());

        using var tx = manager.BeginWrite();
        tx.Nexuses.Create(
            new NexusTypeId(1),
            [new IncidenceMember(new VertexId(1), new RoleId(1)),
             new IncidenceMember(new VertexId(2), new RoleId(1))],
            tx.Incidences,
            tx.VertexIncidenceHeads);

        Action commit = tx.Commit;
        commit.Should().Throw<InvalidOperationException>();
        tx.State.Should().Be(TransactionState.Committed);
        manager.IsFaulted.Should().BeTrue();
        ((Action)(() => manager.BeginRead())).Should().Throw<TransactionException>();
        ((Action)(() => manager.BeginWrite())).Should().Throw<TransactionException>();
        tx.Dispose();
        manager.Dispose();
        wal.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    // ---- Commit hook テスト ----

    [Fact]
    public void OnCommitted_fires_after_commit_in_registration_order()
    {
        var tx = _manager.BeginWrite();
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
        var tx = _manager.BeginWrite();
        var order = new List<int>();
        tx.OnRolledBack(() => order.Add(1));
        tx.OnRolledBack(() => order.Add(2));

        tx.Abort();
        order.Should().Equal(1, 2);
    }

    [Fact]
    public void OnRolledBack_does_not_fire_on_commit()
    {
        var tx = _manager.BeginWrite();
        bool fired = false;
        tx.OnRolledBack(() => fired = true);
        tx.Commit();
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnCommitted_does_not_fire_on_abort()
    {
        var tx = _manager.BeginWrite();
        bool fired = false;
        tx.OnCommitted(() => fired = true);
        tx.Abort();
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnRolledBack_fires_when_disposing_active_transaction()
    {
        var tx = _manager.BeginWrite();
        bool fired = false;
        tx.OnRolledBack(() => fired = true);
        tx.Dispose();
        fired.Should().BeTrue();
    }

    [Fact]
    public void Hook_exception_does_not_break_transaction_or_other_hooks()
    {
        var tx = _manager.BeginWrite();
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
        var tx = _manager.BeginWrite();
        tx.Commit();
        bool fired = false;
        tx.OnCommitted(() => fired = true);
        fired.Should().BeTrue();
    }

    [Fact]
    public void OnRolledBack_registered_after_commit_is_ignored()
    {
        var tx = _manager.BeginWrite();
        tx.Commit();
        bool fired = false;
        tx.OnRolledBack(() => fired = true);
        fired.Should().BeFalse();
    }

    [Fact]
    public void OnRolledBack_registered_after_abort_fires_immediately()
    {
        var tx = _manager.BeginWrite();
        tx.Abort();
        bool fired = false;
        tx.OnRolledBack(() => fired = true);
        fired.Should().BeTrue();
    }

    // ---- テスト用実装 ----

    private sealed class StubVertexStore : IVertexStore
    {
        public long InUseCount => 0;
        public VertexId Allocate(LabelId labelId) => VertexId.Invalid;
        public void Free(VertexId vertexId) { }
        public VertexReadHandle Read(VertexId vertexId) => throw new NotSupportedException();
        public VertexWriteHandle Write(VertexId vertexId) => throw new NotSupportedException();
        public IEnumerable<VertexId> Scan() => [];
        public int CurrentGeneration(long localId) => -1;
        public PropertyCursor EnumerateProperties(VertexId vertexId, IPropertyStore overflowStore)
            => new PropertyCursor(overflowStore, default, PropertyVersionRef.Invalid);
    }

    private sealed class StubEdgeStore : IEdgeStore
    {
        public long InUseCount => 0;
        public EdgeId Create(IVertexStore ns, VertexId src, VertexId tgt, EdgeTypeId type) => EdgeId.Invalid;
        public void Delete(IVertexStore ns, EdgeId edgeId) { }
        public EdgeReadHandle Read(EdgeId edgeId) => throw new NotSupportedException();
        public EdgeWriteHandle Write(EdgeId edgeId) => throw new NotSupportedException();
        public EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore ns) => throw new NotSupportedException();
        public EdgeEnumerator EnumerateNeighbors(VertexId vertexId, IVertexStore ns, EdgeTypeId type, Direction dir) => throw new NotSupportedException();
        public IEnumerable<EdgeId> Scan() => [];
        public PropertyCursor EnumerateProperties(EdgeId edgeId, IPropertyStore overflowStore) => new PropertyCursor(overflowStore, default, PropertyVersionRef.Invalid);
    }

    private sealed class StubPropertyStore : IPropertyStore
    {
        public PropertyVersionRef Create(PropertyAddress address, PropertyCardinality cardinality, in PropertyValue value, PropertyVersionRef currentFirst) => PropertyVersionRef.Invalid;
        public PropertyVersionRef Delete(EntityRef owner, PropertyVersionRef version, PropertyVersionRef currentFirst) => PropertyVersionRef.Invalid;
        public PropertyVersionRecord Read(EntityRef owner, PropertyVersionRef version) => throw new NotSupportedException();
        public PropertyCursor Enumerate(EntityRef owner, PropertyVersionRef firstVersion) => throw new NotSupportedException();
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

    private sealed class StubNexusStore : INexusStore
    {
        public long InUseCount => 0;
        public NexusId Create(NexusTypeId type, ReadOnlySpan<IncidenceMember> members,
            IIncidenceStore incidenceStore, IVertexIncidenceHeadStore vertexHeads) => new(1);
        public void Delete(NexusId nexusId) { }
        public NexusReadHandle Read(NexusId nexusId) => new(
            nexusId, true, new NexusTypeId(1), IncidenceId.Invalid,
            PropertyVersionRef.Invalid, 1, 0);
        public NexusWriteHandle Write(NexusId nexusId) => throw new NotSupportedException();
        public IEnumerable<NexusId> Scan() => [];
        public PropertyCursor EnumerateProperties(NexusId nexusId, IPropertyStore overflowStore)
            => new(overflowStore, default, PropertyVersionRef.Invalid);
    }

    private sealed class ThrowingCoMembershipStore : ICoMembershipBlockStore
    {
        public bool Contains(RoleId originRole, RoleId memberRole) => false;
        public CoMembershipEntry[] GetEntries(VertexId originVertex, RoleId originRole,
            RoleId memberRole, out int count) { count = 0; return []; }
        public void Add(NexusId nexusId, ReadOnlySpan<IncidenceMember> members)
            => throw new InvalidOperationException("publish failure");
        public void Rebuild(INexusStore nexuses, IIncidenceStore incidences) { }
        public void Invalidate() { }
    }
}
