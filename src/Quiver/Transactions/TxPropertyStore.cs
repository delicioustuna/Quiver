using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

// プロパティチェーンは所有Vertexのロック (TxVertexStore で取得済み) で保護される。
// このラッパーは追加ロックなしで全操作を委譲する。
internal sealed class TxPropertyStore : IPropertyStore
{
    private readonly IPropertyStore _inner;
    private readonly TransactionId _txId;
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    // SSN read-sink。プロパティ操作でも ambient sink を維持し、直後/直前の
    // traversal 等が sink を失わないようにする (プロパティ自体は vertex/edge 粒度の read で
    // 既に捕捉されるため、PropertyVersionStore は RecordRead を呼ばない)。
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

    public PropertyVersionRef Create(
        PropertyAddress address,
        PropertyCardinality cardinality,
        in PropertyValue value,
        PropertyVersionRef currentFirst)
    {
        ActivateMvccContext();
        return _inner.Create(address, cardinality, in value, currentFirst);
    }

    public PropertyVersionRef Delete(
        EntityRef owner,
        PropertyVersionRef version,
        PropertyVersionRef currentFirst)
    {
        ActivateMvccContext();
        return _inner.Delete(owner, version, currentFirst);
    }

    public PropertyVersionRecord Read(EntityRef owner, PropertyVersionRef version)
    {
        ActivateMvccContext();
        return _inner.Read(owner, version);
    }

    public PropertyCursor Enumerate(EntityRef owner, PropertyVersionRef firstVersion)
    {
        ActivateMvccContext();
        return _inner.Enumerate(owner, firstVersion);
    }

    internal void FlushMeta()
    {
        if (_inner is PropertyVersionStore store)
            store.FlushMeta();
    }

    private void ActivateMvccContext()
    {
        if (_committed != null)
            MvccContext.Begin(_txId, _snapshot, _committed, _ssn);
    }
}
