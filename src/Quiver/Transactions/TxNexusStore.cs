using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

internal sealed class TxNexusStore : INexusStore
{
    private readonly INexusStore _inner;
    private readonly IIncidenceStore _incidenceStore;
    private readonly IVertexIncidenceHeadStore _vertexHeads;
    private readonly TransactionId _transactionId;
    private readonly SnapshotState _snapshot;
    private readonly VersionVisible _visibility;
    private readonly bool _isReadOnly;
    private readonly ICoMembershipBlockStore? _coMembershipStore;
    private List<(NexusId NexusId, IncidenceMember[] Members)>? _pendingViewAdds;

    internal TxNexusStore(
        INexusStore inner,
        IIncidenceStore incidenceStore,
        IVertexIncidenceHeadStore vertexHeads,
        TransactionId transactionId,
        in SnapshotState snapshot,
        CommittedTxRegistry committed,
        bool isReadOnly,
        ICoMembershipBlockStore? coMembershipStore = null)
    {
        _inner = inner;
        _incidenceStore = incidenceStore;
        _vertexHeads = vertexHeads;
        _transactionId = transactionId;
        _snapshot = snapshot;
        _visibility = (xmin, xmax) => Visibility.IsVisible(xmin, xmax, in _snapshot, _transactionId);
        _isReadOnly = isReadOnly;
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
        EnsureWritable();
        NexusId nexusId = _inner is ITransactionNexusStore store
            ? store.Create(type, members, _incidenceStore, _vertexHeads, _transactionId)
            : _inner.Create(type, members, _incidenceStore, _vertexHeads);
        if (_coMembershipStore is not null)
            (_pendingViewAdds ??= []).Add((nexusId, members.ToArray()));
        return nexusId;
    }

    public void Delete(NexusId nexusId)
    {
        EnsureWritable();
        if (_inner is ITransactionNexusStore store)
            store.Delete(nexusId, _transactionId);
        else
            _inner.Delete(nexusId);
    }

    public NexusReadHandle Read(NexusId nexusId)
    {
        return _inner is ITransactionNexusStore store
            ? store.Read(nexusId, _visibility)
            : _inner.Read(nexusId);
    }

    public NexusWriteHandle Write(NexusId nexusId)
    {
        EnsureWritable();
        return _inner.Write(nexusId);
    }

    public IEnumerable<NexusId> Scan()
    {
        return _inner is ITransactionNexusStore store
            ? store.Scan(_visibility)
            : _inner.Scan();
    }

    public int CurrentGeneration(long sequence)
        => _inner.CurrentGeneration(sequence);

    public bool TryReadRawHeader(long sequence, out RawNexusHeader header)
        => _inner.TryReadRawHeader(sequence, out header);

    public PropertyCursor EnumerateProperties(
        NexusId nexusId,
        IPropertyStore overflowStore)
    {
        NexusReadHandle nexus = Read(nexusId);
        if (!nexus.InUse)
            return new PropertyCursor(overflowStore, default, PropertyVersionRef.Invalid);
        var ownerId = nexusId.Generation == 0 ? nexus.Id : nexusId;
        return overflowStore.Enumerate(EntityRef.From(ownerId), nexus.FirstPropertyRef);
    }

    internal bool HasPendingViewAdds => _pendingViewAdds is { Count: > 0 };

    internal void PublishPendingViewAdds()
    {
        if (_coMembershipStore is null || _pendingViewAdds is null) return;
        try
        {
            foreach (var pending in _pendingViewAdds)
                _coMembershipStore.Add(pending.NexusId, pending.Members);
        }
        catch
        {
            // durable commit後に導出viewだけを部分公開するとprimaryと食い違うため、
            // block全体を無効化して再構築可能なchainへ縮退する。
            _coMembershipStore.Invalidate();
            throw;
        }
        finally
        {
            _pendingViewAdds.Clear();
        }
    }

    internal void RefreshPendingViewAdds()
    {
        if (_coMembershipStore is null) return;
        _pendingViewAdds ??= [];
        _pendingViewAdds.Clear();
        foreach (NexusId nexusId in Scan())
        {
            using NexusReadHandle header = Read(nexusId);
            if (!header.InUse || header.Xmin != _transactionId.Value) continue;
            var members = new List<IncidenceMember>();
            NexusIncidenceEnumerator enumerator = _incidenceStore.EnumerateByNexus(nexusId, _inner);
            while (enumerator.MoveNext())
            {
                IncidenceReadHandle incidence = enumerator.Current;
                members.Add(new IncidenceMember(incidence.VertexId, incidence.RoleId));
            }
            _pendingViewAdds.Add((nexusId, members.ToArray()));
        }
    }

    private void EnsureWritable()
    {
        if (_isReadOnly)
            throw new TransactionException("Cannot mutate nexuses in a read-only transaction.");
    }
}
