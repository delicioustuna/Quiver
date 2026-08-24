using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu;

// ExpandCursor は Yatagarasu.Transactions に定義されている。本クラスはその拡張。

/// <summary>
/// バイナリバックエンド用の expand cursor。
///
/// ソースVertexに隣接ブロックがある場合、<see cref="IAdjacencySegmentStore.OpenCursor"/> で
/// ブロックチェーンを走査する。次数にかかわらず途中で fallback しない。
///
/// ブロックは bulk load / compact 時点のイミュータブルな <em>base</em> ビューのみを覆う。
/// それ以降に作成されたEdgeは <em>delta</em> としてリンクリストに存在する。
/// 隣接ブロックを使い切った後 (tombstone 済み base エントリをスキップしつつ)、リンクリストを
/// 辿って delta を走査する。<c>edgeId &lt; BaseEdgeHwm</c> のエントリは base から既に出力済みなので
/// フィルタし、チェーンは ID 降順なのでその境界を超えた時点で打ち切れる。
/// </summary>
internal sealed class BinaryExpandCursor : ExpandCursor
{
    private readonly ITransaction _tx;
    private VertexId _source;
    private readonly Direction _direction;
    private readonly EdgeTypeId? _typeFilter;
    private readonly BinaryGraphAccessMethods _owner;

    private AdjacencyCursor? _adjCursor;
    private AdjacencyCursor? _deltaCursor;
    private bool _adjActive;     // phase 1: walking the immutable base view
    private bool _opened;
    private bool _validSource;

    private VertexId _neighbor;
    private EdgeId _edgeId;

    internal BinaryExpandCursor(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter,
        BinaryGraphAccessMethods owner)
    {
        _tx = tx;
        _source = source;
        _direction = direction;
        _typeFilter = typeFilter;
        _owner = owner;
        _neighbor = VertexId.Invalid;
        _edgeId = EdgeId.Invalid;
    }

    public override VertexId Neighbor => _neighbor;
    public override EdgeId Edge => _edgeId;
    public override long WeightRaw => _adjActive ? (_adjCursor?.WeightRaw ?? 0) : 0;

    public override bool MoveNext()
    {
        if (!_opened) { Open(); _opened = true; }
        if (!_validSource) return false;

        // まず segment の base ビューを走査する。tombstone をここでフィルタし、
        // base Edgeの削除を読み手から不可視にする。
        if (_adjActive)
        {
            var adj = _tx.AdjacencySegments!;
            while (_adjCursor!.MoveNext())
            {
                // adjacency base は physical edge Sequence だけを持つ。
                // current Generation を付与して primary Read し、candidate validation と
                // logical output の materialization を一回の read で完結させる。
                var physicalEdge = _adjCursor.Edge;
                int generation = _tx.Edges.CurrentGeneration(physicalEdge.Sequence);
                if (generation < 0
                    || (physicalEdge.Generation != 0
                        && physicalEdge.Generation != generation))
                    continue;

                var rid = EdgeId.Create(physicalEdge.Sequence, generation);
                using var edge = _tx.Edges.Read(rid);
                if (!edge.InUse)
                    continue;
                bool sourceIsEndpoint = edge.Source.Sequence == _source.Sequence;
                VertexId neighbor = sourceIsEndpoint ? edge.Target : edge.Source;
                if (adj.IsTombstoned(rid) &&
                    (edge.Type != _adjCursor.Type ||
                      (edge.Source.Sequence != _source.Sequence && edge.Target.Sequence != _source.Sequence) ||
                      neighbor.Sequence != _adjCursor.Neighbor.Sequence))
                {
                    continue;
                }
                _neighbor = neighbor;
                _edgeId = edge.Id;
                return true;
            }
            _adjActive = false; // fall through to phase 2
        }

        while (_deltaCursor!.MoveNext())
        {
            var edge = _tx.Edges.Read(_deltaCursor.Edge);
            if (!edge.InUse)
                continue;
            _neighbor = edge.Source.Sequence == _source.Sequence ? edge.Target : edge.Source;
            _edgeId = edge.Id;
            return true;
        }
        return false;
    }

    private void Open()
    {
        var materializer = new EntityIdentityMaterializer(_tx.Vertices);
        if (!materializer.TryVertex(_source, out _source))
            return;
        _validSource = true;

        var adj = _tx.AdjacencySegments;
        if (adj != null && adj.HasBlock(_source))
        {
            _adjCursor = adj.OpenCursor(_source, _direction, _typeFilter);
            _adjActive = true;
            _deltaCursor = _owner.EdgeDeltas.OpenCursor(
                _tx, _source, _direction, _typeFilter, adj.BaseEdgeHwm);
            return;
        }
        // このソースに対する隣接ブロックが無い — Edgeリンクリストを辿る。
        // fast path が使えなかった頻度を診断で可視化できるよう、カウンタをインクリメントする。
        System.Threading.Interlocked.Increment(ref _owner.FallbackCountInternal);
        _adjActive = false;
        _deltaCursor = _owner.EdgeDeltas.OpenRowCursor(
            _tx, _source, _direction, _typeFilter);
    }

    public override void Dispose()
    {
        _adjCursor?.Dispose();
        _deltaCursor?.Dispose();
    }
}
