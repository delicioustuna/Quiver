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
    private readonly ICoMembershipBlockStore? _coMembershipStore;
    private List<(HyperedgeId HyperedgeId, IncidenceMember[] Members)>? _pendingViewAdds;

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
        SsnContext? ssn = null,
        ICoMembershipBlockStore? coMembershipStore = null)
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
        _coMembershipStore = coMembershipStore;
    }

    public long InUseCount => _inner.InUseCount;
    public long SequenceHighWaterMark => _inner.SequenceHighWaterMark;

    public HyperedgeId Create(
        HyperedgeTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore _,
        INodeIncidenceHeadStore __)
    {
        AcquireNodeLocksAscending(members);
        ActivateMvccContext();
        HyperedgeId hyperedgeId = _inner.Create(type, members, _incidenceStore, _nodeHeads);
        // 導出ビューは commit が durable になってから公開する。同一 transaction 内では
        // pending がある間だけ通常 chain へフォールバックし、未コミット差分も読み落とさない。
        if (_coMembershipStore != null)
        {
            var captured = members.ToArray();
            (_pendingViewAdds ??= []).Add((hyperedgeId, captured));
        }
        return hyperedgeId;
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

    // inline property の読み書きは header の読み書きと同じ locking / SSN 規約に従う。
    // 読みは ReaderWriter モードで shared lock、書きは exclusive lock + SSN write。

    public bool TryGetInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId, out PropertyValue value)
    {
        if (_mode == LockingMode.ReaderWriter)
            AcquireHyperedge(hyperedgeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.TryGetInlineProperty(hyperedgeId, keyId, out value);
    }

    public bool HasInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId)
    {
        if (_mode == LockingMode.ReaderWriter)
            AcquireHyperedge(hyperedgeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.HasInlineProperty(hyperedgeId, keyId);
    }

    public bool SetInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId, in PropertyValue value)
    {
        AcquireHyperedge(hyperedgeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(hyperedgeId.Sequence);
        return _inner.SetInlineProperty(hyperedgeId, keyId, in value);
    }

    public bool RemoveInlineProperty(HyperedgeId hyperedgeId, PropertyKeyId keyId)
    {
        AcquireHyperedge(hyperedgeId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(hyperedgeId.Sequence);
        return _inner.RemoveInlineProperty(hyperedgeId, keyId);
    }

    public PropertyEnumerator EnumerateProperties(HyperedgeId hyperedgeId, IPropertyStore overflowStore)
    {
        if (_mode == LockingMode.ReaderWriter)
            AcquireHyperedge(hyperedgeId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.EnumerateProperties(hyperedgeId, overflowStore);
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

    internal bool HasPendingViewAdds => _pendingViewAdds is { Count: > 0 };

    internal void PublishPendingViewAdds()
    {
        if (_coMembershipStore == null || _pendingViewAdds == null)
            return;
        try
        {
            foreach (var pending in _pendingViewAdds)
                _coMembershipStore.Add(pending.HyperedgeId, pending.Members);
        }
        catch
        {
            // commit は既に durable なので導出ビュー更新の失敗で transaction 結果を
            // 反転させない。不完全な block を無効化し、次回 rebuild まで chain へ縮退する。
            _coMembershipStore.Invalidate();
        }
        finally
        {
            _pendingViewAdds.Clear();
        }
    }

    internal void RefreshPendingViewAdds()
    {
        if (_coMembershipStore == null)
            return;

        _pendingViewAdds ??= [];
        _pendingViewAdds.Clear();
        foreach (HyperedgeId hyperedgeId in _inner.Scan())
        {
            using var header = _inner.Read(hyperedgeId);
            if (!header.InUse || header.Xmin != _txId.Value)
                continue;

            var members = new List<IncidenceMember>();
            var enumerator = _incidenceStore.EnumerateByHyperedge(hyperedgeId, _inner);
            while (enumerator.MoveNext())
            {
                var incidence = enumerator.Current;
                members.Add(new IncidenceMember(incidence.NodeId, incidence.RoleId));
            }
            _pendingViewAdds.Add((hyperedgeId, members.ToArray()));
        }
    }
}
