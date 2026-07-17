using System.Diagnostics;
using Quiver.Core;
using Quiver.Index;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;
using Quiver.Telemetry;

namespace Quiver.Transactions;

internal sealed class Transaction : ITransaction
{
    private readonly IWriteAheadLog _wal;
    private readonly WalWriteSet? _walWriteSet;
    private readonly TransactionManager _manager;
    private readonly TxVertexStore _vertices;
    private readonly TxEdgeStore _edges;
    private readonly TxNexusStore _nexuses;
    private readonly IIncidenceStore _incidences;
    private readonly IVertexIncidenceHeadStore _vertexIncidenceHeads;
    private readonly TxPropertyStore _properties;
    private readonly TxIndexManager _indexes;
    private readonly IAdjacencySegmentStore? _adjacencyStore;
    private readonly ICoMembershipBlockStore? _coMembershipStore;
    private readonly IGraphAccessMethods _access;
    private readonly AbortUndoHandler? _undoHandler;
    private readonly bool _isReadOnly;
    private WriterLease.WriterLeaseHandle? _writerLease;
    private SnapshotRegistry.SnapshotRegistration? _snapshotRegistration;
    private List<Action>? _onCommitted;
    private List<Action>? _onRolledBack;
    private TransactionState _state;
    private int _resourcesReleased;
    private readonly TransactionUsageGuard _usageGuard;
    private long _nextSavepointId;
    private List<(long Id, int Level)>? _savepoints;

    public TransactionId Id { get; }
    public IsolationLevel Level { get; }
    public long SnapshotLsn { get; }
    public TransactionState State => _state;
    public SnapshotState Snapshot { get; }
    public CommittedTxRegistry? Committed { get; }
    public IVertexStore Vertices => _vertices;
    public IEdgeStore Edges => _edges;
    public INexusStore Nexuses => _nexuses;
    public IIncidenceStore Incidences => _incidences;
    public IVertexIncidenceHeadStore VertexIncidenceHeads => _vertexIncidenceHeads;
    public IPropertyStore Properties => _properties;
    public IIndexManager Indexes => _indexes;
    public IAdjacencySegmentStore? AdjacencySegments => _adjacencyStore;
    public ICoMembershipBlockStore? CoMembershipBlocks
        => _nexuses.HasPendingViewAdds ? null : _coMembershipStore;
    public IGraphAccessMethods Access => _access;

    internal Transaction(
        TransactionId id,
        IsolationLevel level,
        long snapshotLsn,
        IWriteAheadLog wal,
        TransactionManager manager,
        IVertexStore vertexStore,
        IEdgeStore edgeStore,
        INexusStore nexusStore,
        IIncidenceStore incidenceStore,
        IVertexIncidenceHeadStore vertexIncidenceHeadStore,
        IPropertyStore propertyStore,
        IIndexManager indexManager,
        IAdjacencySegmentStore? adjacencyStore,
        IGraphAccessMethods? access,
        AbortUndoHandler? undoHandler,
        in SnapshotState snapshot,
        CommittedTxRegistry committed,
        ICoMembershipBlockStore? coMembershipStore,
        PersistentEdgeDeltaStore? edgeDeltas,
        bool isReadOnly,
        WriterLease.WriterLeaseHandle? writerLease,
        SnapshotRegistry.SnapshotRegistration? snapshotRegistration)
    {
        Id = id;
        _usageGuard = new TransactionUsageGuard(id);
        Level = level;
        SnapshotLsn = snapshotLsn;
        Snapshot = snapshot;
        Committed = committed;
        _wal = wal;
        _manager = manager;
        _adjacencyStore = adjacencyStore;
        _coMembershipStore = coMembershipStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _undoHandler = isReadOnly ? null : undoHandler;
        _isReadOnly = isReadOnly;
        _writerLease = writerLease;
        _snapshotRegistration = snapshotRegistration;
        _state = TransactionState.Active;
        _vertices = new TxVertexStore(vertexStore, id, snapshot, committed, isReadOnly);
        _edges = new TxEdgeStore(edgeStore, id, _vertices, snapshot, committed, isReadOnly, edgeDeltas);
        _nexuses = new TxNexusStore(
            nexusStore,
            incidenceStore,
            vertexIncidenceHeadStore,
            id,
            snapshot,
            committed,
            isReadOnly,
            coMembershipStore);
        _incidences = incidenceStore;
        _vertexIncidenceHeads = vertexIncidenceHeadStore;
        _properties = new TxPropertyStore(propertyStore, id, snapshot, committed, isReadOnly);
        _indexes = new TxIndexManager(indexManager, isReadOnly);
        _walWriteSet = isReadOnly ? null : new WalWriteSet(wal, id);
        if (_walWriteSet is not null)
            _wal.ActiveWriteSet = _walWriteSet;
    }

    public TransactionUsageLease EnterUsage() => _usageGuard.Enter();

    public void Commit()
    {
        using var usage = EnterUsage();
        EnsureActive("commit");
        if (_isReadOnly)
        {
            _state = TransactionState.Committed;
            _manager.Complete(Id, committed: true, isReadOnly: true);
            ReleaseResources();
            FireHooks(_onCommitted);
            return;
        }

        _state = TransactionState.Preparing;
        using Activity? activity = QuiverTelemetry.TransactionActivitySource.StartActivity(
            "tx.commit",
            ActivityKind.Internal);
        activity?.SetTag("quiver.tx.id", Id.Value);
        var stopwatch = Stopwatch.StartNew();
        bool durableCommitted = false;
        bool managerCompleted = false;
        try
        {
            _properties.FlushMeta();
            _walWriteSet!.FlushPending();
            long lsn = _wal.Append(WalRecordType.Commit, Id, ReadOnlySpan<byte>.Empty);
            _wal.FlushTo(lsn);
            durableCommitted = true;
            _nexuses.PublishPendingViewAdds();
            _state = TransactionState.Committed;
            _manager.Complete(Id, committed: true, isReadOnly: false);
            managerCompleted = true;
            ReleaseResources();
            _manager.AfterWriteCommitted();
            RecordCommitTelemetry(stopwatch, activity, failedAfterDurability: false);
        }
        catch (Exception exception)
        {
            if (durableCommitted)
            {
                _state = TransactionState.Committed;
                _manager.MarkFaulted();
                if (!managerCompleted)
                {
                    try { _manager.Complete(Id, committed: true, isReadOnly: false); }
                    catch { }
                }
                ReleaseResources();
                try { _manager.AfterWriteCommitted(); } catch { }
                RecordCommitTelemetry(stopwatch, activity, failedAfterDurability: true);
                FireHooks(_onCommitted);
                throw;
            }

            try { RollBackInPlace(); } catch { }
            try { _wal.EvictCoalescedPageImagesFor(Id); } catch { }
            try { _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty); } catch { }
            _state = TransactionState.Aborted;
            try { _manager.Complete(Id, committed: false, isReadOnly: false); } catch { }
            ReleaseResources();
            RecordAbortTelemetry(stopwatch, activity, exception);
            FireHooks(_onRolledBack);
            throw;
        }

        FireHooks(_onCommitted);
    }

    public void Abort()
    {
        using var usage = EnterUsage();
        if (_state is TransactionState.Committed or TransactionState.Aborted) return;
        var stopwatch = Stopwatch.StartNew();
        using Activity? activity = QuiverTelemetry.TransactionActivitySource.StartActivity(
            "tx.abort",
            ActivityKind.Internal);
        activity?.SetTag("quiver.tx.id", Id.Value);

        if (!_isReadOnly)
        {
            RollBackInPlace();
            _wal.EvictCoalescedPageImagesFor(Id);
            _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty);
        }

        _state = TransactionState.Aborted;
        _manager.Complete(Id, committed: false, isReadOnly: _isReadOnly);
        ReleaseResources();
        if (!_isReadOnly)
        {
            QuiverTelemetry.TxAbortCount.Add(1);
            QuiverTelemetry.TxAbortDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
            QuiverEventSource.Log.TxAborted(Id.Value, stopwatch.Elapsed.TotalMilliseconds);
        }
        FireHooks(_onRolledBack);
    }

    private void RollBackInPlace()
    {
        if (_undoHandler is null || _walWriteSet is null) return;
        IReadOnlyCollection<byte[]> beforeImages = _walWriteSet.GetAllBeforeImagesOldestWins();
        if (beforeImages.Count > 0)
            _undoHandler.Undo(beforeImages);
    }

    public SavepointId Savepoint(string? name = null)
    {
        using var usage = EnterUsage();
        EnsureWritable("create a savepoint");
        _properties.FlushMeta();
        int level = _walWriteSet!.PushSavepoint();
        long id = ++_nextSavepointId;
        (_savepoints ??= []).Add((id, level));
        return new SavepointId(id, name);
    }

    public void RollbackTo(SavepointId savepoint)
    {
        using var usage = EnterUsage();
        EnsureWritable("rollback to a savepoint");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");

        int level = _savepoints![index].Level;
        _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);
        IReadOnlyCollection<byte[]> beforeImages = _walWriteSet!.RollbackToSavepoint(level);
        if (_undoHandler is not null && beforeImages.Count > 0)
            _undoHandler.UndoPartial(beforeImages, _walWriteSet);
        _nexuses.RefreshPendingViewAdds();
    }

    public void ReleaseSavepoint(SavepointId savepoint)
    {
        using var usage = EnterUsage();
        EnsureWritable("release a savepoint");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");

        int level = _savepoints![index].Level;
        _savepoints.RemoveRange(index, _savepoints.Count - index);
        _walWriteSet!.ReleaseSavepoint(level);
    }

    private int FindSavepointIndex(long id)
    {
        if (_savepoints is null) return -1;
        for (int index = 0; index < _savepoints.Count; index++)
        {
            if (_savepoints[index].Id == id) return index;
        }
        return -1;
    }

    public void Dispose()
    {
        if (_state == TransactionState.Active)
            Abort();
    }

    public void OnCommitted(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        switch (_state)
        {
            case TransactionState.Committed:
                SafeInvoke(callback);
                break;
            case TransactionState.Aborted:
                break;
            default:
                (_onCommitted ??= []).Add(callback);
                break;
        }
    }

    public void OnRolledBack(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        switch (_state)
        {
            case TransactionState.Aborted:
                SafeInvoke(callback);
                break;
            case TransactionState.Committed:
                break;
            default:
                (_onRolledBack ??= []).Add(callback);
                break;
        }
    }

    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0) return;
        if (_walWriteSet is not null && ReferenceEquals(_wal.ActiveWriteSet, _walWriteSet))
            _wal.ActiveWriteSet = null;
        Interlocked.Exchange(ref _snapshotRegistration, null)?.Dispose();
        Interlocked.Exchange(ref _writerLease, null)?.Dispose();
    }

    private void EnsureActive(string operation)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException(
                $"Cannot {operation}: transaction is not Active.");
    }

    private void EnsureWritable(string operation)
    {
        EnsureActive(operation);
        if (_isReadOnly)
            throw new TransactionException(
                $"Cannot {operation} in a read-only transaction.");
    }

    private void RecordCommitTelemetry(
        Stopwatch stopwatch,
        Activity? activity,
        bool failedAfterDurability)
    {
        QuiverTelemetry.TxCommitCount.Add(1);
        QuiverTelemetry.TxCommitDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
        QuiverEventSource.Log.TxCommit();
        QuiverEventSource.Log.TxCommitted(Id.Value, stopwatch.Elapsed.TotalMilliseconds);
        activity?.SetStatus(
            failedAfterDurability ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
            failedAfterDurability ? "post-commit processing failed" : null);
    }

    private void RecordAbortTelemetry(
        Stopwatch stopwatch,
        Activity? activity,
        Exception exception)
    {
        QuiverTelemetry.TxAbortCount.Add(1);
        QuiverTelemetry.TxAbortDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
        QuiverEventSource.Log.TxAbort();
        QuiverEventSource.Log.TxCommitFailed(
            Id.Value,
            exception.Message,
            exception.GetType().FullName ?? exception.GetType().Name);
        activity?.SetStatus(ActivityStatusCode.Error, "commit failed and rolled back");
    }

    private static void FireHooks(List<Action>? hooks)
    {
        if (hooks is null) return;
        foreach (Action hook in hooks)
            SafeInvoke(hook);
    }

    private static void SafeInvoke(Action callback)
    {
        try { callback(); }
        catch { }
    }

}
