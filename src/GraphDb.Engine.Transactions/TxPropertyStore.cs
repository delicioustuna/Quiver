using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine.Transactions;

// Property chains are protected by their owning node's lock (acquired via TxNodeStore).
// This wrapper simply delegates all operations without additional locking.
internal sealed class TxPropertyStore : IPropertyStore
{
    private readonly IPropertyStore _inner;

    internal TxPropertyStore(IPropertyStore inner) => _inner = inner;

    public PropertyId Create(PropertyKeyId keyId, in PropertyValue value, PropertyId currentFirst)
        => _inner.Create(keyId, in value, currentFirst);

    public PropertyId Delete(PropertyId propId, PropertyId currentFirst)
        => _inner.Delete(propId, currentFirst);

    public PropertyReadHandle Read(PropertyId propId) => _inner.Read(propId);

    public PropertyEnumerator Enumerate(PropertyId firstPropId) => _inner.Enumerate(firstPropId);
}
