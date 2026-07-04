using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

internal sealed class TxHyperedgeStore : IHyperedgeStore
{
    private readonly IHyperedgeStore _inner;
    private readonly IIncidenceStore _incidenceStore;
    private readonly INodeIncidenceHeadStore _nodeHeads;
    private readonly LockManager _hyperedgeLocks;
    private readonly LockManager _nodeLocks;
    private readonly TransactionId _txId;
    private readonly LockingMode _mode;
    private readonly TimeSpan _timeout;
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    private readonly SsnContext? _ssn;

    internal TxHyperedgeStore(
        IHyperedgeStore inner,
        IIncidenceStore incidenceStore,
        INodeIncidenceHeadStore nodeHeads,
        LockManager hyperedgeLocks,
        LockManager nodeLocks,
        TransactionId txId,
        LockingMode mode,
        TimeSpan timeout,
        SnapshotState snapshot = default,
        CommittedTxRegistry? committed = null,
        SsnContext? ssn = null)
    {
        _inner = inner;
        _incidenceStore = incidenceStore;
        _nodeHeads = nodeHeads;
        _hyperedgeLocks = hyperedgeLocks;
        _nodeLocks = nodeLocks;
        _txId = txId;
        _mode = mode;
        _timeout = timeout;
        _snapshot = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _committed = committed;
        _ssn = ssn;
    }

    public long InUseCount => _inner.InUseCount;

    public HyperedgeId Create(
        HyperedgeTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore _,
        INodeIncidenceHeadStore __)
    {
        AcquireNodeLocksAscending(members);
        ActivateMvccContext();
        return _inner.Create(type, members, _incidenceStore, _nodeHeads);
    }

    public void Delete(HyperedgeId hyperedgeId)
    {
        AcquireHyperedge(hyperedgeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(hyperedgeId.Sequence);
        _inner.Delete(hyperedgeId);
    }

    public HyperedgeReadHandle Read(HyperedgeId hyperedgeId)
    {
        if (_mode == LockingMode.ReaderWriter)
            AcquireHyperedge(hyperedgeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.Read(hyperedgeId);
    }

    public HyperedgeWriteHandle Write(HyperedgeId hyperedgeId)
    {
        AcquireHyperedge(hyperedgeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(hyperedgeId.Sequence);
        return _inner.Write(hyperedgeId);
    }

    public IEnumerable<HyperedgeId> Scan()
    {
        ActivateMvccContext();
        return _inner.Scan();
    }

    private void AcquireNodeLocksAscending(ReadOnlySpan<IncidenceMember> members)
    {
        Span<long> sequences = members.Length <= 16
            ? stackalloc long[members.Length]
            : new long[members.Length];
        for (int i = 0; i < members.Length; i++)
            sequences[i] = members[i].NodeId.Sequence;
        sequences.Sort();

        long prev = -1;
        for (int i = 0; i < sequences.Length; i++)
        {
            if (sequences[i] == prev) continue;
            prev = sequences[i];
            if (!_nodeLocks.TryAcquire(sequences[i], _txId, LockMode.Exclusive, _timeout))
                throw new TransactionException(
                    $"Lock timeout acquiring node lock on sequence {sequences[i]} during hyperedge creation.");
        }
    }

    private void AcquireHyperedge(long id, LockMode mode)
    {
        if (!_hyperedgeLocks.TryAcquire(id, _txId, mode, _timeout))
        {
            string what = mode == LockMode.Exclusive ? "write" : "read";
            throw new TransactionException(
                $"Lock timeout acquiring {what} lock on hyperedge {id}.");
        }
    }

    private void ActivateMvccContext()
    {
        if (_committed != null)
            MvccContext.Begin(_txId, _snapshot, _committed, _ssn);
    }

    private void SsnOnWrite(long localId)
    {
        if (_ssn == null || localId < 0) return;
        var id = new EntityId(EntityKind.Hyperedge, localId);
        _ssn.Writes.Add(id);
        _ssn.Reads.Remove(id);
    }
}
