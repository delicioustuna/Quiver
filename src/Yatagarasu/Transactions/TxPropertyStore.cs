using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Transactions;

internal sealed class TxPropertyStore : IPropertyStore
{
    private readonly IPropertyStore _inner;
    private readonly TransactionId _transactionId;
    private readonly SnapshotState _snapshot;
    private readonly VersionVisible _visibility;
    private readonly bool _isReadOnly;

    internal TxPropertyStore(
        IPropertyStore inner,
        TransactionId transactionId,
        in SnapshotState snapshot,
        CommittedTxRegistry committed,
        bool isReadOnly)
    {
        _inner = inner;
        _transactionId = transactionId;
        _snapshot = snapshot;
        _visibility = (xmin, xmax) => Visibility.IsVisible(xmin, xmax, in _snapshot, _transactionId);
        _isReadOnly = isReadOnly;
    }

    public PropertyVersionRef Create(
        PropertyAddress address,
        PropertyCardinality cardinality,
        in PropertyValue value,
        PropertyVersionRef currentFirst)
    {
        EnsureWritable();
        return _inner is ITransactionPropertyStore store
            ? store.Create(address, cardinality, in value, currentFirst, _transactionId, _visibility)
            : _inner.Create(address, cardinality, in value, currentFirst);
    }

    public PropertyVersionRef Delete(
        EntityRef owner,
        PropertyVersionRef version,
        PropertyVersionRef currentFirst)
    {
        EnsureWritable();
        return _inner is ITransactionPropertyStore store
            ? store.Delete(owner, version, currentFirst, _transactionId, _visibility)
            : _inner.Delete(owner, version, currentFirst);
    }

    public PropertyVersionRecord Read(
        EntityRef owner,
        PropertyVersionRef version)
    {
        return _inner is ITransactionPropertyStore store
            ? store.Read(owner, version, _visibility)
            : _inner.Read(owner, version);
    }

    public PropertyVersionRecord Read(PropertyVersionRef version)
    {
        return _inner is ITransactionPropertyStore store
            ? store.Read(version, _visibility)
            : _inner.Read(version);
    }

    public PropertyCursor Enumerate(
        EntityRef owner,
        PropertyVersionRef firstVersion)
    {
        return new PropertyCursor(this, owner, firstVersion);
    }

    internal void FlushMeta()
    {
        if (_inner is PropertyVersionStore store)
            store.FlushMeta();
    }

    private void EnsureWritable()
    {
        if (_isReadOnly)
            throw new TransactionException("Cannot mutate properties in a read-only transaction.");
    }
}
