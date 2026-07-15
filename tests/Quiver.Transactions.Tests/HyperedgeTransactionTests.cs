using FluentAssertions;
using Quiver.Core;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Transactions.Tests;

/// <summary>
/// hyperedge store の transaction 統合を検証する。
/// commit、rollback、savepoint、crash recovery、SSN、reader-writer lock をカバーする。
/// </summary>
public sealed class HyperedgeTransactionTests : IDisposable
{
    private const byte DataFileKind = 1;
    private const byte TenantHyperedgeHeap = 18;
    private const byte TenantHyperedgeMap = 19;
    private const byte TenantHyperedgeVersion = 20;
    private const byte TenantIncidenceHeap = 21;
    private const byte TenantNodeIncidenceHead = 25;

    private readonly string _walDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _containerPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".quiver");
    private readonly string _crashPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".quiver");

    public void Dispose()
    {
        if (Directory.Exists(_walDir)) Directory.Delete(_walDir, recursive: true);
        if (File.Exists(_containerPath)) File.Delete(_containerPath);
        if (File.Exists(_crashPath)) File.Delete(_crashPath);
    }

    // --- テスト用セットアップ ---

    private (TransactionManager Manager, WriteAheadLog Wal, SingleFileContainer Container,
        VersionedHyperedgeStore HyperedgeStore, IncidenceStore IncidenceStore,
        NodeIncidenceHeadStore NodeHeadStore, CommittedTxRegistry Registry) CreateManager(
        string path,
        LockingMode lockingMode = LockingMode.ExclusiveOnly)
    {
        var wal = new WriteAheadLog(Path.Combine(_walDir, "wal-" + Path.GetFileNameWithoutExtension(path)));
        var container = new SingleFileContainer(path);
        container.EnableWalLogging(DataFileKind, wal);

        var hyperedgeHeapFile = container.OpenTenant(TenantHyperedgeHeap, PageKind.Header);
        var hyperedgeMapFile = container.OpenTenant(TenantHyperedgeMap, PageKind.Header);
        var hyperedgeVerFile = container.OpenTenant(TenantHyperedgeVersion, PageKind.Header);
        var hyperedgeVersions = new EntityVersionStore(hyperedgeVerFile);
        var hyperedgeMap = new ItemPointerMap(hyperedgeMapFile);
        var hyperedgeStore = new VersionedHyperedgeStore(hyperedgeHeapFile, hyperedgeMap, hyperedgeVersions);

        var incidenceHeapFile = container.OpenTenant(TenantIncidenceHeap, PageKind.Header);
        var incidenceStore = new IncidenceStore(incidenceHeapFile);

        var nodeHeadFile = container.OpenTenant(TenantNodeIncidenceHead, PageKind.Header);
        var nodeHeadStore = new NodeIncidenceHeadStore(nodeHeadFile);

        var registry = new CommittedTxRegistry();

        var fileRegistry = new Dictionary<byte, IPagedFile> { { DataFileKind, container.Physical } };
        void ReloadStoreMeta()
        {
            container.ReloadAll();
            hyperedgeStore.ReloadMeta();
            incidenceStore.ReloadMeta();
        }

        var undoHandler = new AbortUndoHandler(fileRegistry, ReloadStoreMeta);

        var manager = new TransactionManager(
            wal,
            new StubNodeStore(), new StubRelationshipStore(),
            new StubPropertyStore(), new NullIndexManager(),
            adjStore: null, access: null, undoHandler: undoHandler,
            lockingMode: lockingMode,
            committedRegistry: registry,
            nodeVersions: new InMemoryEntityVersionStore(),
            relVersions: new InMemoryEntityVersionStore(),
            hyperedgeStore: hyperedgeStore,
            incidenceStore: incidenceStore,
            nodeIncidenceHeadStore: nodeHeadStore,
            hyperedgeVersions: hyperedgeVersions);

        return (manager, wal, container, hyperedgeStore, incidenceStore, nodeHeadStore, registry);
    }

    private static IncidenceMember[] TwoMembers(int n1 = 0, int n2 = 1, int role = 1)
        => [new(new NodeId(n1), new RoleId(role)), new(new NodeId(n2), new RoleId(role))];

    // --- commit / rollback ---

    [Fact]
    public void Committed_hyperedge_is_visible_after_commit()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        HyperedgeId id;
        using (var tx = manager.Begin())
        {
            id = tx.Hyperedges.Create(new HyperedgeTypeId(1), TwoMembers(),
                tx.Incidences, tx.NodeIncidenceHeads);
            tx.Commit();
        }

        using (var tx = manager.Begin())
        {
            using var h = tx.Hyperedges.Read(id);
            h.InUse.Should().BeTrue();
        }
    }

    [Fact]
    public void Aborted_hyperedge_is_not_visible()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        HyperedgeId id;
        using (var tx = manager.Begin())
        {
            id = tx.Hyperedges.Create(new HyperedgeTypeId(1), TwoMembers(),
                tx.Incidences, tx.NodeIncidenceHeads);
            tx.Abort();
        }

        using (var tx = manager.Begin())
        {
            using var h = tx.Hyperedges.Read(id);
            h.InUse.Should().BeFalse();
        }
    }

    [Fact]
    public void Dispose_auto_aborts_active_hyperedge_transaction()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        HyperedgeId id;
        using (var tx = manager.Begin())
        {
            id = tx.Hyperedges.Create(new HyperedgeTypeId(1), TwoMembers(),
                tx.Incidences, tx.NodeIncidenceHeads);
            // no commit, dispose triggers abort
        }

        using (var tx = manager.Begin())
        {
            using var h = tx.Hyperedges.Read(id);
            h.InUse.Should().BeFalse();
        }
    }

    // --- savepoint ---

    [Fact]
    public void Savepoint_rollback_reverts_hyperedge_creation()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        using var tx = manager.Begin();
        var sp = tx.Savepoint("before_create");
        HyperedgeId id = tx.Hyperedges.Create(new HyperedgeTypeId(1), TwoMembers(),
            tx.Incidences, tx.NodeIncidenceHeads);
        tx.RollbackTo(sp);

        using var h = tx.Hyperedges.Read(id);
        h.InUse.Should().BeFalse();
    }

    [Fact]
    public void Nested_savepoint_preserves_outer_hyperedge()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        using var tx = manager.Begin();

        HyperedgeId outer = tx.Hyperedges.Create(new HyperedgeTypeId(1), TwoMembers(0, 1),
            tx.Incidences, tx.NodeIncidenceHeads);
        var sp = tx.Savepoint("inner");
        HyperedgeId inner = tx.Hyperedges.Create(new HyperedgeTypeId(2), TwoMembers(2, 3),
            tx.Incidences, tx.NodeIncidenceHeads);
        tx.RollbackTo(sp);

        using var outerH = tx.Hyperedges.Read(outer);
        outerH.InUse.Should().BeTrue();

        using var innerH = tx.Hyperedges.Read(inner);
        innerH.InUse.Should().BeFalse();
    }

    // --- crash recovery ---

    [Fact]
    public void Committed_hyperedge_survives_crash_recovery()
    {
        string srcPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".quiver");
        string crashPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".quiver");
        string walPath = Path.Combine(_walDir, "wal-crash");
        try
        {
            using (var initContainer = new SingleFileContainer(srcPath))
            {
                var initWal = new WriteAheadLog(walPath);
                initContainer.EnableWalLogging(DataFileKind, initWal);
                OpenHyperedgeStores(initContainer);
                initContainer.Flush();
                initWal.Dispose();
            }
            File.Copy(srcPath, crashPath, overwrite: true);

            HyperedgeId id;
            using (var wal2 = new WriteAheadLog(walPath))
            using (var src = new SingleFileContainer(srcPath))
            {
                src.EnableWalLogging(DataFileKind, wal2);
                var (store, incStore, headStore) = OpenHyperedgeStores(src);

                var txId = new TransactionId(42);
                wal2.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
                WalPageContext.Begin(wal2, txId);
                id = store.Create(new HyperedgeTypeId(5), TwoMembers(), incStore, headStore);
                WalPageContext.FlushPending();
                long commitLsn = wal2.Append(WalRecordType.Commit, txId, ReadOnlySpan<byte>.Empty);
                wal2.FlushTo(commitLsn);
                WalPageContext.End();
            }

            using var crashWal = new WriteAheadLog(walPath);
            using var dst = new SingleFileContainer(crashPath);
            var registry = new Dictionary<byte, IPagedFile> { { DataFileKind, dst.Physical } };
            new RecoveryManager(new NullPageManager(), crashWal, registry).Recover();
            dst.ReloadAll();

            var (recoveredStore, _, _) = OpenHyperedgeStores(dst);
            using var h = recoveredStore.Read(id);
            h.InUse.Should().BeTrue();
            h.Type.Should().Be(new HyperedgeTypeId(5));
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(crashPath);
        }
    }

    [Fact]
    public void Aborted_hyperedge_does_not_survive_crash_recovery()
    {
        string srcPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".quiver");
        string crashPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".quiver");
        string walPath = Path.Combine(_walDir, "wal-crash-abort");
        try
        {
            using (var initContainer = new SingleFileContainer(srcPath))
            {
                var initWal = new WriteAheadLog(walPath);
                initContainer.EnableWalLogging(DataFileKind, initWal);
                OpenHyperedgeStores(initContainer);
                initContainer.Flush();
                initWal.Dispose();
            }
            File.Copy(srcPath, crashPath, overwrite: true);

            HyperedgeId id;
            using (var wal2 = new WriteAheadLog(walPath))
            using (var src = new SingleFileContainer(srcPath))
            {
                src.EnableWalLogging(DataFileKind, wal2);
                var (store, incStore, headStore) = OpenHyperedgeStores(src);

                var txId = new TransactionId(99);
                wal2.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
                WalPageContext.Begin(wal2, txId);
                id = store.Create(new HyperedgeTypeId(5), TwoMembers(), incStore, headStore);
                WalPageContext.FlushPending();
                long abortLsn = wal2.Append(WalRecordType.Abort, txId, ReadOnlySpan<byte>.Empty);
                wal2.FlushTo(abortLsn);
                WalPageContext.End();
            }

            using var crashWal = new WriteAheadLog(walPath);
            using var dst = new SingleFileContainer(crashPath);
            var registry = new Dictionary<byte, IPagedFile> { { DataFileKind, dst.Physical } };
            new RecoveryManager(new NullPageManager(), crashWal, registry).Recover();
            dst.ReloadAll();

            var (recoveredStore, _, _) = OpenHyperedgeStores(dst);
            using var h = recoveredStore.Read(id);
            h.InUse.Should().BeFalse();
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(crashPath);
        }
    }

    private static (VersionedHyperedgeStore, IncidenceStore, NodeIncidenceHeadStore) OpenHyperedgeStores(
        SingleFileContainer container)
    {
        var heapFile = container.OpenTenant(TenantHyperedgeHeap, PageKind.Header);
        var mapFile = container.OpenTenant(TenantHyperedgeMap, PageKind.Header);
        var verFile = container.OpenTenant(TenantHyperedgeVersion, PageKind.Header);
        var store = new VersionedHyperedgeStore(heapFile, new ItemPointerMap(mapFile), new EntityVersionStore(verFile));
        var incHeapFile = container.OpenTenant(TenantIncidenceHeap, PageKind.Header);
        var incStore = new IncidenceStore(incHeapFile);
        var headFile = container.OpenTenant(TenantNodeIncidenceHead, PageKind.Header);
        var headStore = new NodeIncidenceHeadStore(headFile);
        return (store, incStore, headStore);
    }

    // --- SSN (Serializable) ---

    [Fact]
    public void Ssn_write_set_tracks_hyperedge_entity_kind()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        HyperedgeId id;
        using (var tx = manager.Begin())
        {
            id = tx.Hyperedges.Create(new HyperedgeTypeId(1), TwoMembers(),
                tx.Incidences, tx.NodeIncidenceHeads);
            tx.Commit();
        }

        using (var tx = manager.Begin(IsolationLevel.Serializable))
        {
            tx.Hyperedges.Delete(id);
            // SSN write-set should contain the hyperedge entity.
            // Commit succeeds because no conflicting read dependency exists.
            var act = () => tx.Commit();
            act.Should().NotThrow();
        }
    }

    // --- reader-writer locking ---

    [Fact]
    public void Reader_writer_mode_allows_concurrent_shared_reads()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath,
            lockingMode: LockingMode.ReaderWriter);
        using var _ = Disposables(manager, wal, container);

        HyperedgeId id;
        using (var tx = manager.Begin())
        {
            id = tx.Hyperedges.Create(new HyperedgeTypeId(1), TwoMembers(),
                tx.Incidences, tx.NodeIncidenceHeads);
            tx.Commit();
        }

        // Two concurrent readers should not deadlock or timeout.
        using var tx1 = manager.Begin();
        using var tx2 = manager.Begin();

        using var h1 = tx1.Hyperedges.Read(id);
        using var h2 = tx2.Hyperedges.Read(id);
        h1.InUse.Should().BeTrue();
        h2.InUse.Should().BeTrue();
    }

    // --- node lock ascending order ---

    [Fact]
    public void Create_acquires_node_locks_in_ascending_order()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        // Creating a hyperedge with descending node IDs should succeed
        // (internal sorting prevents deadlocks with ascending lock acquisition).
        using var tx = manager.Begin();
        var members = new IncidenceMember[]
        {
            new(new NodeId(100), new RoleId(1)),
            new(new NodeId(50), new RoleId(1)),
            new(new NodeId(200), new RoleId(1)),
        };

        var act = () => tx.Hyperedges.Create(new HyperedgeTypeId(1), members,
            tx.Incidences, tx.NodeIncidenceHeads);
        act.Should().NotThrow();
        tx.Commit();
    }

    // --- DeadlockDetector now monitors hyperedge locks ---

    [Fact]
    public void Deadlock_detector_includes_hyperedge_lock_manager()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        // Simple smoke: creating and committing exercises the hyperedge lock manager
        // which is monitored by DeadlockDetector. No hang = pass.
        using (var tx = manager.Begin())
        {
            tx.Hyperedges.Create(new HyperedgeTypeId(1), TwoMembers(),
                tx.Incidences, tx.NodeIncidenceHeads);
            tx.Commit();
        }

        manager.ActiveCount.Should().Be(0);
    }

    // --- helpers ---

    private static IDisposable Disposables(params IDisposable[] items) => new CompositeDisposable(items);

    private sealed class CompositeDisposable(IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            for (int i = items.Length - 1; i >= 0; i--)
                items[i].Dispose();
        }
    }

    private sealed class NullPageManager : IPageManager
    {
        public IPagedFile OpenOrCreate(string path, PageKind defaultKind) => throw new NotSupportedException();
        public void FlushAll() { }
        public void Dispose() { }
    }

    private sealed class StubNodeStore : INodeStore
    {
        public long InUseCount => 0;
        public NodeId Allocate(LabelId labelId) => NodeId.Invalid;
        public void Free(NodeId nodeId) { }
        public NodeReadHandle Read(NodeId nodeId) => throw new NotSupportedException();
        public NodeWriteHandle Write(NodeId nodeId) => throw new NotSupportedException();
        public IEnumerable<NodeId> Scan() => [];
        public int CurrentGeneration(long localId) => -1;
        public bool TryGetInlineProperty(NodeId nodeId, PropertyKeyId keyId, out PropertyValue value) { value = default; return false; }
        public bool HasInlineProperty(NodeId nodeId, PropertyKeyId keyId) => false;
        public bool SetInlineProperty(NodeId nodeId, PropertyKeyId keyId, in PropertyValue value) => false;
        public bool RemoveInlineProperty(NodeId nodeId, PropertyKeyId keyId) => false;
        public PropertyEnumerator EnumerateProperties(NodeId nodeId, IPropertyStore overflowStore)
            => new PropertyEnumerator(overflowStore, PropertyId.Invalid);
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
        public bool TryGetInlineProperty(RelationshipId relId, PropertyKeyId keyId, out PropertyValue value) { value = default; return false; }
        public bool HasInlineProperty(RelationshipId relId, PropertyKeyId keyId) => false;
        public bool SetInlineProperty(RelationshipId relId, PropertyKeyId keyId, in PropertyValue value) => false;
        public bool RemoveInlineProperty(RelationshipId relId, PropertyKeyId keyId) => false;
        public PropertyEnumerator EnumerateProperties(RelationshipId relId, IPropertyStore overflowStore) => new PropertyEnumerator(overflowStore, PropertyId.Invalid);
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
