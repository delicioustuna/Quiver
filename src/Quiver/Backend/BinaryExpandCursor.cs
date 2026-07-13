using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

// ExpandCursor は Quiver.Transactions に定義されている。本クラスはその拡張。

/// <summary>
/// バイナリバックエンド用の expand cursor。
///
/// ソースノードに隣接ブロックがある場合、<see cref="IAdjacencyBlockStore.OpenCursor"/> で
/// ブロックチェーンを走査する。次数にかかわらず途中で fallback しない。
///
/// ブロックは bulk load / compact 時点のイミュータブルな <em>base</em> ビューのみを覆う。
/// それ以降に作成されたリレーションシップは <em>delta</em> としてリンクリストに存在する。
/// 隣接ブロックを使い切った後 (tombstone 済み base エントリをスキップしつつ)、リンクリストを
/// 辿って delta を走査する。<c>relId &lt; BaseRelHwm</c> のエントリは base から既に出力済みなので
/// フィルタし、チェーンは ID 降順なのでその境界を超えた時点で打ち切れる。
/// </summary>
internal sealed class BinaryExpandCursor : ExpandCursor
{
    private readonly ITransaction _tx;
    private readonly NodeId _source;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly BinaryGraphAccessMethods _owner;

    private AdjacencyCursor? _adjCursor;
    private AdjacencyCursor? _deltaCursor;
    private bool _adjActive;     // phase 1: walking the immutable base view
    private bool _opened;

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
        _neighbor = NodeId.Invalid;
        _relId = RelationshipId.Invalid;
    }

    public override NodeId Neighbor => _neighbor;
    public override RelationshipId Relationship => _relId;
    public override long WeightRaw => _adjActive ? (_adjCursor?.WeightRaw ?? 0) : 0;

    public override bool MoveNext()
    {
        if (!_opened) { Open(); _opened = true; }

        // Phase 1: 隣接ブロック経由の base ビュー走査。tombstone をここでフィルタし、
        // base リレーションシップの削除を読み手から不可視にする。
        if (_adjActive)
        {
            var adj = _tx.AdjacencyBlocks!;
            while (_adjCursor!.MoveNext())
            {
                var rid = _adjCursor.Relationship;
                var rel = _tx.Relationships.Read(rid);
                if (!rel.InUse) continue;
                bool sourceIsEndpoint = rel.Source.Sequence == _source.Sequence;
                NodeId neighbor = sourceIsEndpoint ? rel.Target : rel.Source;
                if (adj.IsTombstoned(rid) &&
                    (rel.Type != _adjCursor.Type ||
                      (rel.Source.Sequence != _source.Sequence && rel.Target.Sequence != _source.Sequence) ||
                      neighbor.Sequence != _adjCursor.Neighbor.Sequence))
                {
                    continue;
                }
                _neighbor = neighbor;
                _relId = rel.Id;
                return true;
            }
            _adjActive = false; // fall through to phase 2
        }

        while (_deltaCursor!.MoveNext())
        {
            var rel = _tx.Relationships.Read(_deltaCursor.Relationship);
            if (!rel.InUse)
                continue;
            _neighbor = rel.Source.Sequence == _source.Sequence ? rel.Target : rel.Source;
            _relId = rel.Id;
            return true;
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
            _deltaCursor = _owner.RelationshipDeltas.OpenCursor(
                _tx, _source, _direction, _typeFilter, adj.BaseRelHwm);
            return;
        }
        // このソースに対する隣接ブロックが無い — リレーションシップリンクリストを辿る。
        // fast path が使えなかった頻度を診断で可視化できるよう、カウンタをインクリメントする。
        System.Threading.Interlocked.Increment(ref _owner.FallbackCountInternal);
        _adjActive = false;
        _deltaCursor = _owner.RelationshipDeltas.OpenCursor(
            _tx, _source, _direction, _typeFilter, adj?.BaseRelHwm ?? 0);
    }

    public override void Dispose()
    {
        _adjCursor?.Dispose();
        _deltaCursor?.Dispose();
    }
}
