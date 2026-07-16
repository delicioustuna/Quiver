using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

internal sealed class TxNexusStore : INexusStore
{
    private readonly INexusStore _inner;
    private readonly IIncidenceStore _incidenceStore;
    private readonly IVertexIncidenceHeadStore _vertexHeads;
    private readonly LockManager _nexusLocks;
    private readonly LockManager _vertexLocks;
    private readonly TransactionId _txId;
    private readonly LockingMode _mode;
    private readonly TimeSpan _timeout;
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    private readonly SsnContext? _ssn;
    private readonly ICoMembershipBlockStore? _coMembershipStore;
    private List<(NexusId NexusId, IncidenceMember[] Members)>? _pendingViewAdds;

    internal TxNexusStore(
        INexusStore inner,
        IIncidenceStore incidenceStore,
        IVertexIncidenceHeadStore vertexHeads,
        LockManager nexusLocks,
        LockManager vertexLocks,
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
        _vertexHeads = vertexHeads;
        _nexusLocks = nexusLocks;
        _vertexLocks = vertexLocks;
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

    public NexusId Create(
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        IIncidenceStore _,
        IVertexIncidenceHeadStore __)
    {
        AcquireVertexLocksAscending(members);
        ActivateMvccContext();
        NexusId nexusId = _inner.Create(type, members, _incidenceStore, _vertexHeads);
        // 導出ビューは commit が durable になってから公開する。同一 transaction 内では
        // pending がある間だけ通常 chain へフォールバックし、未コミット差分も読み落とさない。
        if (_coMembershipStore != null)
        {
            var captured = members.ToArray();
            (_pendingViewAdds ??= []).Add((nexusId, captured));
        }
        return nexusId;
    }

    public void Delete(NexusId nexusId)
    {
        AcquireNexus(nexusId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(nexusId.Sequence);
        _inner.Delete(nexusId);
    }

    public NexusReadHandle Read(NexusId nexusId)
    {
        if (_mode == LockingMode.ReaderWriter)
            AcquireNexus(nexusId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.Read(nexusId);
    }

    public NexusWriteHandle Write(NexusId nexusId)
    {
        AcquireNexus(nexusId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(nexusId.Sequence);
        return _inner.Write(nexusId);
    }

    public IEnumerable<NexusId> Scan()
    {
        ActivateMvccContext();
        return _inner.Scan();
    }

    // inline property の読み書きは header の読み書きと同じ locking / SSN 規約に従う。
    // 読みは ReaderWriter モードで shared lock、書きは exclusive lock + SSN write。

    public bool TryGetInlineProperty(NexusId nexusId, PropertyKeyId keyId, out PropertyValue value)
    {
        if (_mode == LockingMode.ReaderWriter)
            AcquireNexus(nexusId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.TryGetInlineProperty(nexusId, keyId, out value);
    }

    public bool HasInlineProperty(NexusId nexusId, PropertyKeyId keyId)
    {
        if (_mode == LockingMode.ReaderWriter)
            AcquireNexus(nexusId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.HasInlineProperty(nexusId, keyId);
    }

    public bool SetInlineProperty(NexusId nexusId, PropertyKeyId keyId, in PropertyValue value)
    {
        AcquireNexus(nexusId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(nexusId.Sequence);
        return _inner.SetInlineProperty(nexusId, keyId, in value);
    }

    public bool RemoveInlineProperty(NexusId nexusId, PropertyKeyId keyId)
    {
        AcquireNexus(nexusId.Sequence, LockMode.Exclusive);
        ActivateMvccContext();
        SsnOnWrite(nexusId.Sequence);
        return _inner.RemoveInlineProperty(nexusId, keyId);
    }

    public PropertyEnumerator EnumerateProperties(NexusId nexusId, IPropertyStore overflowStore)
    {
        if (_mode == LockingMode.ReaderWriter)
            AcquireNexus(nexusId.Sequence, LockMode.Shared);
        ActivateMvccContext();
        return _inner.EnumerateProperties(nexusId, overflowStore);
    }

    private void AcquireVertexLocksAscending(ReadOnlySpan<IncidenceMember> members)
    {
        Span<long> sequences = members.Length <= 16
            ? stackalloc long[members.Length]
            : new long[members.Length];
        for (int i = 0; i < members.Length; i++)
            sequences[i] = members[i].VertexId.Sequence;
        sequences.Sort();

        long prev = -1;
        for (int i = 0; i < sequences.Length; i++)
        {
            if (sequences[i] == prev) continue;
            prev = sequences[i];
            if (!_vertexLocks.TryAcquire(sequences[i], _txId, LockMode.Exclusive, _timeout))
                throw new TransactionException(
                    $"Lock timeout acquiring vertex lock on sequence {sequences[i]} during nexus creation.");
        }
    }

    private void AcquireNexus(long id, LockMode mode)
    {
        if (!_nexusLocks.TryAcquire(id, _txId, mode, _timeout))
        {
            string what = mode == LockMode.Exclusive ? "write" : "read";
            throw new TransactionException(
                $"Lock timeout acquiring {what} lock on nexus {id}.");
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
        var id = new EntityId(EntityKind.Nexus, localId);
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
                _coMembershipStore.Add(pending.NexusId, pending.Members);
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
        foreach (NexusId nexusId in _inner.Scan())
        {
            using var header = _inner.Read(nexusId);
            if (!header.InUse || header.Xmin != _txId.Value)
                continue;

            var members = new List<IncidenceMember>();
            var enumerator = _incidenceStore.EnumerateByNexus(nexusId, _inner);
            while (enumerator.MoveNext())
            {
                var incidence = enumerator.Current;
                members.Add(new IncidenceMember(incidence.VertexId, incidence.RoleId));
            }
            _pendingViewAdds.Add((nexusId, members.ToArray()));
        }
    }
}
