using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

// ExpandCursor is now defined in Quiver.Transactions (BA-3); this class extends it.

/// <summary>
/// Binary-backend expand cursor.
///
/// When the source has an adjacency block, the cursor walks the block
/// chain via <see cref="IAdjacencyBlockStore.OpenCursor"/>, which never falls
/// back mid-iteration regardless of degree.
///
/// A block covers only the immutable <em>base</em> view captured at
/// bulk-load / compact time. Relationships created after that point live in
/// the relationship linked list as <em>delta</em>. After exhausting the
/// adjacency block (skipping tombstoned base entries) the cursor continues
/// through the linked list, filtering out anything with
/// <c>relId &lt; BaseRelHwm</c> — those were already emitted from the base
/// view, and crucially the chain is monotonically descending so we can break
/// as soon as we cross that boundary.
/// </summary>
internal sealed class BinaryExpandCursor : ExpandCursor
{
    private readonly ITransaction _tx;
    private readonly NodeId _source;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly BinaryGraphAccessMethods _owner;

    private AdjacencyCursor? _adjCursor;
    private bool _adjActive;     // phase 1: walking the immutable base view
    private bool _opened;
    private long _baseRelHwm;    // delta-vs-base partition for phase 2

    private RelationshipId _nextRelId;
    private NodeId _neighbor;
    private RelationshipId _relId;

    internal BinaryExpandCursor(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter,
        BinaryGraphAccessMethods owner)
    {
        _tx = tx;
        _source = source;
        _direction = direction;
        _typeFilter = typeFilter;
        _owner = owner;
        _nextRelId = RelationshipId.Invalid;
        _neighbor = NodeId.Invalid;
        _relId = RelationshipId.Invalid;
    }

    public override NodeId Neighbor => _neighbor;
    public override RelationshipId Relationship => _relId;
    public override long WeightRaw => _adjActive ? (_adjCursor?.WeightRaw ?? 0) : 0;

    public override bool MoveNext()
    {
        if (!_opened) { Open(); _opened = true; }

        // Phase 1: base view via adjacency block. Tombstones get filtered here
        // so deletes of base relationships are invisible to readers.
        if (_adjActive)
        {
            var adj = _tx.AdjacencyBlocks!;
            while (_adjCursor!.MoveNext())
            {
                var rid = _adjCursor.Relationship;
                if (adj.IsTombstoned(rid)) continue;
                _neighbor = _adjCursor.Neighbor;
                _relId = rid;
                return true;
            }
            _adjActive = false; // fall through to phase 2
        }

        // Phase 2: delta walk over the relationship linked list. Skip base
        // entries (already emitted) by comparing against the watermark. The
        // chain is strictly descending by id (newest at head), so once we
        // cross into base ids every remaining entry is also base — break.
        while (_nextRelId.IsValid)
        {
            if (_baseRelHwm > 0 && _nextRelId.Sequence < _baseRelHwm) // ARCH-5b: hwm 比較は Sequence
                return false;

            var rel = _tx.Relationships.Read(_nextRelId);
            var thisRel = _nextRelId;
            _nextRelId = rel.Source == _source ? rel.SourceNext : rel.TargetNext;

            // FT-26: MVCC visibility 判定で invisible になった record はスキップ。
            if (!rel.InUse) continue;

            bool typeOk = !_typeFilter.HasValue || rel.Type == _typeFilter.Value;
            bool dirOk = _direction switch
            {
                Direction.Outgoing => rel.Source == _source,
                Direction.Incoming => rel.Target == _source,
                _ => true,
            };
            if (typeOk && dirOk)
            {
                _neighbor = rel.Source == _source ? rel.Target : rel.Source;
                _relId = thisRel;
                return true;
            }
        }
        return false;
    }

    private void Open()
    {
        var adj = _tx.AdjacencyBlocks;
        if (adj != null && adj.HasBlock(_source))
        {
            _adjCursor = adj.OpenCursor(_source, _direction, _typeFilter);
            _adjActive = true;
            _baseRelHwm = adj.BaseRelHwm;
            // PW-14: prime the delta walk too — after the base phase we'll
            // resume from the linked-list head and skip ids < BaseRelHwm.
            _nextRelId = _tx.Nodes.Read(_source).FirstRelationshipId;
            return;
        }
        // このソースに対する隣接ブロックが無い — リレーションシップリンクリストを辿る。
        // fast path が使えなかった頻度を診断で可視化できるよう、カウンタをインクリメントする。
        System.Threading.Interlocked.Increment(ref _owner.FallbackCountInternal);
        _adjActive = false;
        _baseRelHwm = adj?.BaseRelHwm ?? 0;
        _nextRelId = _tx.Nodes.Read(_source).FirstRelationshipId;
    }

    public override void Dispose()
    {
        _adjCursor?.Dispose();
    }
}
