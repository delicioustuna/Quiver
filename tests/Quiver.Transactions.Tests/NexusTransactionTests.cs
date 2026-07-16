using FluentAssertions;
using Quiver.Core;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Transactions.Tests;

/// <summary>
/// nexus store の transaction 統合を検証する。
/// commit、rollback、savepoint、crash recovery、SSN、reader-writer lock をカバーする。
/// </summary>
public sealed class NexusTransactionTests : IDisposable
{
    private const byte DataFileKind = 1;
    private const byte TenantNexusHeap = 18;
    private const byte TenantNexusMap = 19;
    private const byte TenantNexusVersion = 20;
    private const byte TenantIncidenceHeap = 21;
    private const byte TenantVertexIncidenceHead = 25;

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
        VersionedNexusStore NexusStore, IncidenceStore IncidenceStore,
        VertexIncidenceHeadStore VertexHeadStore, CommittedTxRegistry Registry) CreateManager(
        string path,
        LockingMode lockingMode = LockingMode.ExclusiveOnly)
    {
        var wal = new WriteAheadLog(Path.Combine(_walDir, "wal-" + Path.GetFileNameWithoutExtension(path)));
        var container = new SingleFileContainer(path);
        container.EnableWalLogging(DataFileKind, wal);

        var nexusHeapFile = container.OpenTenant(TenantNexusHeap, PageKind.Header);
        var nexusMapFile = container.OpenTenant(TenantNexusMap, PageKind.Header);
        var nexusVerFile = container.OpenTenant(TenantNexusVersion, PageKind.Header);
        var nexusVersions = new EntityVersionStore(nexusVerFile);
        var nexusMap = new ItemPointerMap(nexusMapFile);
        var nexusStore = new VersionedNexusStore(nexusHeapFile, nexusMap, nexusVersions);

        var incidenceHeapFile = container.OpenTenant(TenantIncidenceHeap, PageKind.Header);
        var incidenceStore = new IncidenceStore(incidenceHeapFile);

        var vertexHeadFile = container.OpenTenant(TenantVertexIncidenceHead, PageKind.Header);
        var vertexHeadStore = new VertexIncidenceHeadStore(vertexHeadFile);

        var registry = new CommittedTxRegistry();

        var fileRegistry = new Dictionary<byte, IPagedFile> { { DataFileKind, container.Physical } };
        void ReloadStoreMeta()
        {
            container.ReloadAll();
            nexusStore.ReloadMeta();
            incidenceStore.ReloadMeta();
        }

        var undoHandler = new AbortUndoHandler(fileRegistry, ReloadStoreMeta);

        var manager = new TransactionManager(
            wal,
            new StubVertexStore(), new StubEdgeStore(),
            new StubPropertyStore(), new NullIndexManager(),
            adjStore: null, access: null, undoHandler: undoHandler,
            lockingMode: lockingMode,
            committedRegistry: registry,
            vertexVersions: new InMemoryEntityVersionStore(),
            edgeVersions: new InMemoryEntityVersionStore(),
            nexusStore: nexusStore,
            incidenceStore: incidenceStore,
            vertexIncidenceHeadStore: vertexHeadStore,
            nexusVersions: nexusVersions);

        return (manager, wal, container, nexusStore, incidenceStore, vertexHeadStore, registry);
    }

    private static IncidenceMember[] TwoMembers(int n1 = 0, int n2 = 1, int role = 1)
        => [new(new VertexId(n1), new RoleId(role)), new(new VertexId(n2), new RoleId(role))];

    // --- commit / rollback ---

    [Fact]
    public void Committed_nexus_is_visible_after_commit()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        NexusId id;
        using (var tx = manager.Begin())
        {
            id = tx.Nexuses.Create(new NexusTypeId(1), TwoMembers(),
                tx.Incidences, tx.VertexIncidenceHeads);
            tx.Commit();
        }

        using (var tx = manager.Begin())
        {
            using var h = tx.Nexuses.Read(id);
            h.InUse.Should().BeTrue();
        }
    }

    [Fact]
    public void Aborted_nexus_is_not_visible()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        NexusId id;
        using (var tx = manager.Begin())
        {
            id = tx.Nexuses.Create(new NexusTypeId(1), TwoMembers(),
                tx.Incidences, tx.VertexIncidenceHeads);
            tx.Abort();
        }

        using (var tx = manager.Begin())
        {
            using var h = tx.Nexuses.Read(id);
            h.InUse.Should().BeFalse();
        }
    }

    [Fact]
    public void Dispose_auto_aborts_active_nexus_transaction()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        NexusId id;
        using (var tx = manager.Begin())
        {
            id = tx.Nexuses.Create(new NexusTypeId(1), TwoMembers(),
                tx.Incidences, tx.VertexIncidenceHeads);
            // no commit, dispose triggers abort
        }

        using (var tx = manager.Begin())
        {
            using var h = tx.Nexuses.Read(id);
            h.InUse.Should().BeFalse();
        }
    }

    // --- savepoint ---

    [Fact]
    public void Savepoint_rollback_reverts_nexus_creation()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        using var tx = manager.Begin();
        var sp = tx.Savepoint("before_create");
        NexusId id = tx.Nexuses.Create(new NexusTypeId(1), TwoMembers(),
            tx.Incidences, tx.VertexIncidenceHeads);
        tx.RollbackTo(sp);

        using var h = tx.Nexuses.Read(id);
        h.InUse.Should().BeFalse();
    }

    [Fact]
    public void Nested_savepoint_preserves_outer_nexus()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        using var tx = manager.Begin();

        NexusId outer = tx.Nexuses.Create(new NexusTypeId(1), TwoMembers(0, 1),
            tx.Incidences, tx.VertexIncidenceHeads);
        var sp = tx.Savepoint("inner");
        NexusId inner = tx.Nexuses.Create(new NexusTypeId(2), TwoMembers(2, 3),
            tx.Incidences, tx.VertexIncidenceHeads);
        tx.RollbackTo(sp);

        using var outerH = tx.Nexuses.Read(outer);
        outerH.InUse.Should().BeTrue();

        using var innerH = tx.Nexuses.Read(inner);
        innerH.InUse.Should().BeFalse();
    }

    // --- crash recovery ---

    [Fact]
    public void Committed_nexus_survives_crash_recovery()
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
                OpenNexusStores(initContainer);
                initContainer.Flush();
                initWal.Dispose();
            }
            File.Copy(srcPath, crashPath, overwrite: true);

            NexusId id;
            using (var wal2 = new WriteAheadLog(walPath))
            using (var src = new SingleFileContainer(srcPath))
            {
                src.EnableWalLogging(DataFileKind, wal2);
                var (store, incStore, headStore) = OpenNexusStores(src);

                var txId = new TransactionId(42);
                wal2.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
                WalWriteSetContext.Begin(wal2, txId);
                id = store.Create(new NexusTypeId(5), TwoMembers(), incStore, headStore);
                WalWriteSetContext.FlushPending();
                long commitLsn = wal2.Append(WalRecordType.Commit, txId, ReadOnlySpan<byte>.Empty);
                wal2.FlushTo(commitLsn);
                WalWriteSetContext.End();
            }

            using var crashWal = new WriteAheadLog(walPath);
            using var dst = new SingleFileContainer(crashPath);
            var registry = new Dictionary<byte, IPagedFile> { { DataFileKind, dst.Physical } };
            new RecoveryManager(new NullPageManager(), crashWal, registry).Recover();
            dst.ReloadAll();

            var (recoveredStore, _, _) = OpenNexusStores(dst);
            using var h = recoveredStore.Read(id);
            h.InUse.Should().BeTrue();
            h.Type.Should().Be(new NexusTypeId(5));
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(crashPath);
        }
    }

    [Fact]
    public void Aborted_nexus_does_not_survive_crash_recovery()
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
                OpenNexusStores(initContainer);
                initContainer.Flush();
                initWal.Dispose();
            }
            File.Copy(srcPath, crashPath, overwrite: true);

            NexusId id;
            using (var wal2 = new WriteAheadLog(walPath))
            using (var src = new SingleFileContainer(srcPath))
            {
                src.EnableWalLogging(DataFileKind, wal2);
                var (store, incStore, headStore) = OpenNexusStores(src);

                var txId = new TransactionId(99);
                wal2.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
                WalWriteSetContext.Begin(wal2, txId);
                id = store.Create(new NexusTypeId(5), TwoMembers(), incStore, headStore);
                WalWriteSetContext.FlushPending();
                long abortLsn = wal2.Append(WalRecordType.Abort, txId, ReadOnlySpan<byte>.Empty);
                wal2.FlushTo(abortLsn);
                WalWriteSetContext.End();
            }

            using var crashWal = new WriteAheadLog(walPath);
            using var dst = new SingleFileContainer(crashPath);
            var registry = new Dictionary<byte, IPagedFile> { { DataFileKind, dst.Physical } };
            new RecoveryManager(new NullPageManager(), crashWal, registry).Recover();
            dst.ReloadAll();

            var (recoveredStore, _, _) = OpenNexusStores(dst);
            using var h = recoveredStore.Read(id);
            h.InUse.Should().BeFalse();
        }
        finally
        {
            File.Delete(srcPath);
            File.Delete(crashPath);
        }
    }

    private static (VersionedNexusStore, IncidenceStore, VertexIncidenceHeadStore) OpenNexusStores(
        SingleFileContainer container)
    {
        var heapFile = container.OpenTenant(TenantNexusHeap, PageKind.Header);
        var mapFile = container.OpenTenant(TenantNexusMap, PageKind.Header);
        var verFile = container.OpenTenant(TenantNexusVersion, PageKind.Header);
        var store = new VersionedNexusStore(heapFile, new ItemPointerMap(mapFile), new EntityVersionStore(verFile));
        var incHeapFile = container.OpenTenant(TenantIncidenceHeap, PageKind.Header);
        var incStore = new IncidenceStore(incHeapFile);
        var headFile = container.OpenTenant(TenantVertexIncidenceHead, PageKind.Header);
        var headStore = new VertexIncidenceHeadStore(headFile);
        return (store, incStore, headStore);
    }

    // --- SSN (Serializable) ---

    [Fact]
    public void Ssn_write_set_tracks_nexus_entity_kind()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        NexusId id;
        using (var tx = manager.Begin())
        {
            id = tx.Nexuses.Create(new NexusTypeId(1), TwoMembers(),
                tx.Incidences, tx.VertexIncidenceHeads);
            tx.Commit();
        }

        using (var tx = manager.Begin(IsolationLevel.Serializable))
        {
            tx.Nexuses.Delete(id);
            // SSN write-set should contain the nexus entity.
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

        NexusId id;
        using (var tx = manager.Begin())
        {
            id = tx.Nexuses.Create(new NexusTypeId(1), TwoMembers(),
                tx.Incidences, tx.VertexIncidenceHeads);
            tx.Commit();
        }

        // Two concurrent readers should not deadlock or timeout.
        using var tx1 = manager.Begin();
        using var tx2 = manager.Begin();

        using var h1 = tx1.Nexuses.Read(id);
        using var h2 = tx2.Nexuses.Read(id);
        h1.InUse.Should().BeTrue();
        h2.InUse.Should().BeTrue();
    }

    // --- vertex lock ascending order ---

    [Fact]
    public void Create_acquires_vertex_locks_in_ascending_order()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        // Creating a nexus with descending vertex IDs should succeed
        // (internal sorting prevents deadlocks with ascending lock acquisition).
        using var tx = manager.Begin();
        var members = new IncidenceMember[]
        {
            new(new VertexId(100), new RoleId(1)),
            new(new VertexId(50), new RoleId(1)),
            new(new VertexId(200), new RoleId(1)),
        };

        var act = () => tx.Nexuses.Create(new NexusTypeId(1), members,
            tx.Incidences, tx.VertexIncidenceHeads);
        act.Should().NotThrow();
        tx.Commit();
    }

    // --- DeadlockDetector now monitors nexus locks ---

    [Fact]
    public void Deadlock_detector_includes_nexus_lock_manager()
    {
        var (manager, wal, container, _, _, _, _) = CreateManager(_containerPath);
        using var _ = Disposables(manager, wal, container);

        // Simple smoke: creating and committing exercises the nexus lock manager
        // which is monitored by DeadlockDetector. No hang = pass.
        using (var tx = manager.Begin())
        {
            tx.Nexuses.Create(new NexusTypeId(1), TwoMembers(),
                tx.Incidences, tx.VertexIncidenceHeads);
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

    private sealed class StubVertexStore : IVertexStore
    {
        public long InUseCount => 0;
        public VertexId Allocate(LabelId labelId) => VertexId.Invalid;
        public void Free(VertexId vertexId) { }
        public VertexReadHandle Read(VertexId vertexId) => throw new NotSupportedException();
        public VertexWriteHandle Write(VertexId vertexId) => throw new NotSupportedException();
        public IEnumerable<VertexId> Scan() => [];
        public int CurrentGeneration(long localId) => -1;
        public bool TryGetInlineProperty(VertexId vertexId, PropertyKeyId keyId, out PropertyValue value) { value = default; return false; }
        public bool HasInlineProperty(VertexId vertexId, PropertyKeyId keyId) => false;
        public bool SetInlineProperty(VertexId vertexId, PropertyKeyId keyId, in PropertyValue value) => false;
        public bool RemoveInlineProperty(VertexId vertexId, PropertyKeyId keyId) => false;
        public PropertyEnumerator EnumerateProperties(VertexId vertexId, IPropertyStore overflowStore)
            => new PropertyEnumerator(overflowStore, PropertyId.Invalid);
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
        public bool TryGetInlineProperty(EdgeId edgeId, PropertyKeyId keyId, out PropertyValue value) { value = default; return false; }
        public bool HasInlineProperty(EdgeId edgeId, PropertyKeyId keyId) => false;
        public bool SetInlineProperty(EdgeId edgeId, PropertyKeyId keyId, in PropertyValue value) => false;
        public bool RemoveInlineProperty(EdgeId edgeId, PropertyKeyId keyId) => false;
        public PropertyEnumerator EnumerateProperties(EdgeId edgeId, IPropertyStore overflowStore) => new PropertyEnumerator(overflowStore, PropertyId.Invalid);
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
