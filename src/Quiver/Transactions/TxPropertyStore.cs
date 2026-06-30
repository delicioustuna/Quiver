using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

// プロパティチェーンは所有ノードのロック (TxNodeStore で取得済み) で保護される。
// このラッパーは追加ロックなしで全操作を委譲する。
internal sealed class TxPropertyStore : IPropertyStore
{
    private readonly IPropertyStore _inner;
    private readonly TransactionId _txId;
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    // SSN read-sink。プロパティ操作でも ambient sink を維持し、直後/直前の
    // traversal 等が sink を失わないようにする (プロパティ自体は node/rel 粒度の read で
    // 既に捕捉されるため、PropertyStore は RecordRead を呼ばない)。
    private readonly ISsnReadSink? _ssn;

    internal TxPropertyStore(IPropertyStore inner, TransactionId txId = default,
        SnapshotState snapshot = default, CommittedTxRegistry? committed = null,
        ISsnReadSink? ssn = null)
    {
        _inner = inner;
        _txId = txId;
        _snapshot = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _committed = committed;
        _ssn = ssn;
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
            MvccContext.Begin(_txId, _snapshot, _committed, _ssn);
    }
}
