using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Transactions;

// Property chains are protected by their owning node's lock (acquired via TxNodeStore).
// This wrapper simply delegates all operations without additional locking.
internal sealed class TxPropertyStore : IPropertyStore
{
    private readonly IPropertyStore _inner;
    private readonly TransactionId _txId;
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;

    internal TxPropertyStore(IPropertyStore inner, TransactionId txId = default,
        SnapshotState snapshot = default, CommittedTxRegistry? committed = null)
    {
        _inner = inner;
        _txId = txId;
        _snapshot = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _committed = committed;
    }

    public PropertyId Create(PropertyKeyId keyId, in PropertyValue value, PropertyId currentFirst)
    {
        ActivateMvccContext();
        return _inner.Create(keyId, in value, currentFirst);
    }

    public PropertyId Delete(PropertyId propId, PropertyId currentFirst)
    {
        ActivateMvccContext();
        return _inner.Delete(propId, currentFirst);
    }

    public PropertyReadHandle Read(PropertyId propId)
    {
        ActivateMvccContext();
        return _inner.Read(propId);
    }

    public PropertyEnumerator Enumerate(PropertyId firstPropId)
    {
        ActivateMvccContext();
        return _inner.Enumerate(firstPropId);
    }

    private void ActivateMvccContext()
    {
        if (_committed != null)
            MvccContext.Begin(_txId, _snapshot, _committed);
    }
}
