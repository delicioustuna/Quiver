using System.Collections.Concurrent;
using Yatagarasu.Core;
using Yatagarasu.Index;
using Yatagarasu.Index.FullText;
using Yatagarasu.Storage.Records;
using Yatagarasu.Storage.Wal;
using Yatagarasu.Telemetry;

namespace Yatagarasu.Transactions;

internal sealed class TransactionManager : ITransactionManager
{
    private readonly IWriteAheadLog _wal;
    private readonly IVertexStore _vertexStore;
    private readonly IEdgeStore _edgeStore;
    private readonly INexusStore _nexusStore;
    private readonly IIncidenceStore _incidenceStore;
    private readonly IVertexIncidenceHeadStore _vertexIncidenceHeadStore;
    private readonly IPropertyStore _propertyStore;
    private readonly IIndexManager _indexManager;
    private readonly PersistentEdgeDeltaStore? _edgeDeltas;
    private IAdjacencySegmentStore? _adjacencyStore;
    private readonly ICoMembershipBlockStore? _coMembershipStore;
    private readonly IGraphAccessMethods _access;
    private readonly AbortUndoHandler? _undoHandler;
    private readonly ConcurrentDictionary<long, Transaction> _active = new();
    private readonly object _snapshotGate = new();
    private readonly CommittedTxRegistry _committed;
    private readonly WriterLease _writerLease;
    private readonly SnapshotRegistry _snapshotRegistry;
    private long _nextTxId;
    private int _faulted;

    private Checkpointer? _checkpointer;
    private long _checkpointThresholdBytes;
    private long _lastCheckpointBytes;
    private readonly object _checkpointGate = new();
    private AdaptiveCheckpointController? _adaptiveController;
    private long _lastSampledWalBytes;
    private readonly IDisposable _activeTxCountRegistration;
    private readonly IDisposable _checkpointThresholdRegistration;
    private readonly IDisposable _snapshotEventSourceRegistration;
    private readonly IDisposable _snapshotMeterRegistration;

    internal TransactionManager(
        IWriteAheadLog wal,
        IVertexStore vertexStore,
        IEdgeStore edgeStore,
        IPropertyStore propertyStore,
        IIndexManager indexManager,
        IAdjacencySegmentStore? adjacencyStore = null,
        IGraphAccessMethods? access = null,
        AbortUndoHandler? undoHandler = null,
        bool rejectConcurrentWriters = false,
        TimeSpan? writerTimeout = null,
        CommittedTxRegistry? committedRegistry = null,
        INexusStore? nexusStore = null,
        IIncidenceStore? incidenceStore = null,
        IVertexIncidenceHeadStore? vertexIncidenceHeadStore = null,
        ICoMembershipBlockStore? coMembershipStore = null,
        PersistentEdgeDeltaStore? edgeDeltas = null,
        SnapshotRegistry? snapshotRegistry = null)
    {
        _wal = wal;
        _vertexStore = vertexStore;
        _edgeStore = edgeStore;
        _nexusStore = nexusStore ?? NullNexusStore.Instance;
        _incidenceStore = incidenceStore ?? NullIncidenceStore.Instance;
        _vertexIncidenceHeadStore = vertexIncidenceHeadStore ?? NullVertexIncidenceHeadStore.Instance;
        _propertyStore = propertyStore;
        _indexManager = indexManager;
        _edgeDeltas = edgeDeltas;
        _adjacencyStore = adjacencyStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _undoHandler = undoHandler;
        _committed = committedRegistry ?? new CommittedTxRegistry();
        _coMembershipStore = coMembershipStore;
        _snapshotRegistry = snapshotRegistry ?? new SnapshotRegistry(
            warningSink: diagnostics => YatagarasuEventSource.Log.OldSnapshotDetected(
                diagnostics.ActiveCount,
                diagnostics.OldestAge.TotalSeconds,
                diagnostics.OldestStartLocation ?? "unknown",
                diagnostics.OldestCommittedHighWater ?? 0));
        _writerLease = new WriterLease(
            writerTimeout ?? TimeSpan.FromSeconds(5),
            rejectConcurrentWriters);
        _nextTxId = TransactionId.Bootstrap.Value + 1;
        _activeTxCountRegistration =
            YatagarasuEventSource.Log.RegisterActiveTxCountProvider(() => _active.Count);
        _checkpointThresholdRegistration =
            YatagarasuEventSource.Log.RegisterCheckpointThresholdProvider(() => CurrentCheckpointThresholdBytes);
        _snapshotEventSourceRegistration =
            YatagarasuEventSource.Log.RegisterSnapshotProvider(
                () => _snapshotRegistry.Diagnostics.ActiveCount,
                () => _snapshotRegistry.Diagnostics.OldestAge.TotalSeconds);
        _snapshotMeterRegistration =
            YatagarasuTelemetry.RegisterSnapshotProvider(
                () => _snapshotRegistry.Diagnostics.ActiveCount,
                () => _snapshotRegistry.Diagnostics.OldestAge.TotalSeconds);
    }

    internal CommittedTxRegistry CommittedRegistry => _committed;
    internal SnapshotRegistry Snapshots => _snapshotRegistry;
    internal INexusStore NexusStore => _nexusStore;
    internal IIncidenceStore IncidenceStore => _incidenceStore;
    internal IVertexIncidenceHeadStore VertexIncidenceHeadStore => _vertexIncidenceHeadStore;
    internal FullTextSegmentIndex? FullTextSegments { get; set; }

    public int ActiveCount => _active.Count;

    internal bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>
    /// トランザクション外の bulk/schema/maintenance publish を通常の writer と
    /// 同じ database-instance lease に参加させる。
    /// </summary>
    internal IDisposable AcquireMutationLease()
        => AcquireMutationLease(ownerTransactionId: null);

    internal IDisposable AcquireMutationLease(TransactionId? ownerTransactionId)
    {
        ThrowIfFaulted();
        if (ownerTransactionId is { } owner
            && _writerLease.ActiveWriterId == owner)
        {
            return NoopDisposable.Instance;
        }

        return _writerLease.Acquire(AllocateTransactionId());
    }

    public ITransaction BeginRead()
    {
        ThrowIfFaulted();
        lock (_snapshotGate)
        {
            TransactionId txId = AllocateTransactionId();
            SnapshotState snapshot = _committed.Capture(_writerLease.ActiveWriterId);
            SnapshotRegistry.SnapshotRegistration registration =
                _snapshotRegistry.Register(in snapshot);
            try
            {
                var transaction = CreateTransaction(
                    txId,
                    snapshot,
                    isReadOnly: true,
                    writerLease: null,
                    registration);
                _active[txId.Value] = transaction;
                return transaction;
            }
            catch
            {
                registration.Dispose();
                throw;
            }
        }
    }

    public ITransaction BeginWrite()
    {
        ThrowIfFaulted();
        TransactionId txId = AllocateTransactionId();
        WriterLease.WriterLeaseHandle lease = _writerLease.Acquire(txId);
        try
        {
            lock (_snapshotGate)
            {
                ThrowIfFaulted();
                SnapshotState snapshot = _committed.Capture(_writerLease.ActiveWriterId);
                _wal.Append(WalRecordType.BeginWrite, txId, ReadOnlySpan<byte>.Empty);
                var transaction = CreateTransaction(
                    txId,
                    snapshot,
                    isReadOnly: false,
                    lease,
                    registration: null);
                _active[txId.Value] = transaction;
                return transaction;
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private Transaction CreateTransaction(
        TransactionId txId,
        in SnapshotState snapshot,
        bool isReadOnly,
        WriterLease.WriterLeaseHandle? writerLease,
        SnapshotRegistry.SnapshotRegistration? registration)
        => new(
            txId,
            _wal.FlushedLsn,
            _wal,
            this,
            _vertexStore,
            _edgeStore,
            _nexusStore,
            _incidenceStore,
            _vertexIncidenceHeadStore,
            _propertyStore,
            _indexManager,
            _adjacencyStore,
            _access,
            _undoHandler,
            in snapshot,
            _committed,
            _coMembershipStore,
            _edgeDeltas,
            isReadOnly,
            writerLease,
            registration);

    internal void Complete(
        TransactionId txId,
        bool committed,
        bool isReadOnly)
    {
        lock (_snapshotGate)
        {
            if (!isReadOnly)
            {
                if (committed)
                    _committed.MarkCommitted(txId);
                else
                    _committed.MarkAborted(txId);
            }
            _active.TryRemove(txId.Value, out _);
        }

    }

    internal void AfterWriteCommitted()
    {
        RecordAdaptiveSample();
        MaybeCheckpoint();
    }

    internal void MarkFaulted()
        => Interlocked.Exchange(ref _faulted, 1);

    internal void AdvanceNextTxIdAtLeast(long minimum)
    {
        long current = Volatile.Read(ref _nextTxId);
        while (minimum > current)
        {
            long previous = Interlocked.CompareExchange(ref _nextTxId, minimum, current);
            if (previous == current) return;
            current = previous;
        }
    }

    internal long PeekNextTxId() => Volatile.Read(ref _nextTxId);

    internal long GetVisibilityHorizon()
    {
        long fallback = _committed.CommittedHighWater;
        long oldest = _snapshotRegistry.OldestCommittedHighWater(fallback);
        return oldest == long.MaxValue ? long.MaxValue : oldest + 1;
    }

    public long OldestActiveLsn
    {
        get
        {
            long oldest = long.MaxValue;
            foreach (Transaction transaction in _active.Values)
                oldest = Math.Min(oldest, transaction.SnapshotLsn);
            return oldest == long.MaxValue ? _wal.FlushedLsn : oldest;
        }
    }

    internal void EnableCheckpointing(Checkpointer checkpointer, long thresholdBytes)
    {
        _checkpointer = checkpointer;
        _checkpointThresholdBytes = thresholdBytes;
        _lastCheckpointBytes = _wal.BytesWritten;
        _lastSampledWalBytes = _wal.BytesWritten;
    }

    internal void SetAdaptiveController(AdaptiveCheckpointController? controller)
    {
        _adaptiveController = controller;
        _lastSampledWalBytes = _wal.BytesWritten;
    }

    internal void SetFixedThreshold(long thresholdBytes)
        => _checkpointThresholdBytes = thresholdBytes;

    internal long CurrentCheckpointThresholdBytes
    {
        get
        {
            AdaptiveCheckpointController? controller = _adaptiveController;
            if (controller is { HasEnoughSamples: true })
                return controller.CurrentThresholdBytes;
            return _checkpointThresholdBytes;
        }
    }

    private void RecordAdaptiveSample()
    {
        AdaptiveCheckpointController? controller = _adaptiveController;
        if (controller is null) return;
        long current = _wal.BytesWritten;
        long previous = Interlocked.Exchange(ref _lastSampledWalBytes, current);
        long delta = current - previous;
        if (delta > 0) controller.RecordTxBytes(delta);
    }

    private void MaybeCheckpoint()
    {
        Checkpointer? checkpointer = _checkpointer;
        if (checkpointer is null) return;
        long threshold = CurrentCheckpointThresholdBytes;
        if (threshold <= 0) return;
        if (_wal.BytesWritten - Volatile.Read(ref _lastCheckpointBytes) < threshold) return;

        if (!Monitor.TryEnter(_checkpointGate)) return;
        try
        {
            threshold = CurrentCheckpointThresholdBytes;
            if (threshold <= 0) return;
            if (_wal.BytesWritten - _lastCheckpointBytes < threshold) return;
            // threshold/manual/close のどの入口でも同じ writer lease を取得する。
            // reader は lease を使わないため checkpoint の前後で待たされない。
            using IDisposable checkpointLease = AcquireMutationLease();
            checkpointer.Checkpoint();
            Volatile.Write(ref _lastCheckpointBytes, _wal.BytesWritten);
        }
        finally
        {
            Monitor.Exit(_checkpointGate);
        }
    }

    internal void SwapAdjacencyStore(
        IAdjacencySegmentStore? next,
        bool writerLeaseHeld = false)
    {
        if (!writerLeaseHeld && _writerLease.ActiveWriterId is not null)
            throw new TransactionException("Cannot replace adjacency storage while a writer is active.");
        _adjacencyStore = next;
    }

    internal void RequestCheckpoint(bool writerLeaseHeld = false)
    {
        Checkpointer? checkpointer = _checkpointer;
        if (checkpointer is null) return;
        Monitor.Enter(_checkpointGate);
        try
        {
            using IDisposable? checkpointLease =
                writerLeaseHeld ? null : AcquireMutationLease();
            checkpointer.Checkpoint();
            Volatile.Write(ref _lastCheckpointBytes, _wal.BytesWritten);
        }
        finally
        {
            Monitor.Exit(_checkpointGate);
        }
    }

    private TransactionId AllocateTransactionId()
        => new(Interlocked.Increment(ref _nextTxId) - 1);

    private void ThrowIfFaulted()
    {
        if (IsFaulted)
            throw new TransactionException(
                "The database instance is faulted after a durable commit publication failure.");
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    public void Dispose()
    {
        _writerLease.Dispose();
        _activeTxCountRegistration.Dispose();
        _checkpointThresholdRegistration.Dispose();
        _snapshotEventSourceRegistration.Dispose();
        _snapshotMeterRegistration.Dispose();
    }
}
