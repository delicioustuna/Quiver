using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// Edgeストアを逐次スキャンし、事前構築した <see cref="FrontierSet"/> で
/// ソースVertexに突合する 1 ホップ展開オペレータ。frontier がエッジ総数に対して大きい場合に
/// Vertexごとのリンクリスト / 隣接ブロック走査より有利になる — N 本の独立リストを辿ると
/// ページ局所性が崩壊するのに対し、逐次スキャンは各ページを 1 度だけ参照する。
/// </summary>
/// <remarks>
/// <para>
/// 出力スキーマは選択した <see cref="ExpandOutputMode"/> に応じて
/// <see cref="ExpandOperator"/> と一致するため、後段の Filter / Project にそのまま置換可能。
/// </para>
/// <para>
/// ソースオペレータは <see cref="Open"/> で完全に排出して frontier set を構築する。
/// スキャンは frontier が大きいときにのみ有効であり、set 構築は O(N) なので同じトレードオフ。
/// </para>
/// </remarks>
internal sealed class EdgeScanExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceVertexColumn;
    private readonly Direction _direction;
    private readonly EdgeTypeId? _typeFilter;
    private readonly ExpandOutputMode _outputMode;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;

    private ITransaction? _tx;
    private FrontierSet? _frontier;
    private IEnumerator<EdgeId>? _scanEnumerator;

    public EdgeScanExpandOperator(
        IPhysicalOperator source,
        int sourceVertexColumn,
        Direction direction,
        EdgeTypeId? typeFilter,
        ExpandOutputMode outputMode)
    {
        _source = source;
        _sourceVertexColumn = sourceVertexColumn;
        _direction = direction;
        _typeFilter = typeFilter;
        _outputMode = outputMode;
        (_buffer, _schema) = outputMode switch
        {
            ExpandOutputMode.NeighborOnly => (new TupleSlot[1], new TupleSchema([
                new ColumnDefinition("neighbor", TupleSlotType.VertexId)])),
            ExpandOutputMode.NeighborAndEdge => (new TupleSlot[2], new TupleSchema([
                new ColumnDefinition("edge", TupleSlotType.EdgeId),
                new ColumnDefinition("neighbor", TupleSlotType.VertexId)])),
            _ => (new TupleSlot[3], new TupleSchema([
                new ColumnDefinition("source", TupleSlotType.VertexId),
                new ColumnDefinition("edge", TupleSlotType.EdgeId),
                new ColumnDefinition("neighbor", TupleSlotType.VertexId)])),
        };
    }

    public TupleSchema Schema => _schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);

        // ソースを排出して frontier set を構築する。bitmap vs hashset の判定のため
        // id を list にも集めるが、Build 後 list は不要。
        var ids = new List<VertexId>(64);
        long max = -1;
        while (_source.MoveNext())
        {
            if (!TryMaterialize(new VertexId(_source.Current[_sourceVertexColumn].LongValue), out var id))
                continue;
            // Why not Sequence-only: vacuum 後に再利用された slot を seed と誤って突合すると、
            // stale frontier が現在の別Vertexの edge を展開してしまう。
            ids.Add(id);
            if (id.Sequence > max) max = id.Sequence;
        }
        _frontier = FrontierSet.Build(ids, max);
        _scanEnumerator = tx.Edges.Scan().GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_scanEnumerator == null || _frontier == null) return false;
        while (_scanEnumerator.MoveNext())
        {
            var edgeId = _scanEnumerator.Current;
            var edge = _tx!.Edges.Read(edgeId);

            if (_typeFilter.HasValue && edge.Type != _typeFilter.Value) continue;

            // スキャンが検査した全Edgeを論理読み取りとして計上する。
            // Vertexごとのパスの RowsProduced と比較してスキャン局所性の効果を評価できる。
            var s = Statistics;
            s.EdgeScanRecords++;

            if (!TryMaterialize(edge.Source, out VertexId edgeSource)
                || !TryMaterialize(edge.Target, out VertexId edgeTarget))
                continue;
            VertexId source, neighbor;
            switch (_direction)
            {
                case Direction.Outgoing:
                    if (!_frontier.Contains(edgeSource)) { Statistics = s; continue; }
                    source = edgeSource; neighbor = edgeTarget;
                    break;
                case Direction.Incoming:
                    if (!_frontier.Contains(edgeTarget)) { Statistics = s; continue; }
                    source = edgeTarget; neighbor = edgeSource;
                    break;
                default: // Both
                    if (_frontier.Contains(edgeSource))
                    {
                        source = edgeSource; neighbor = edgeTarget;
                    }
                    else if (_frontier.Contains(edgeTarget))
                    {
                        source = edgeTarget; neighbor = edgeSource;
                    }
                    else { Statistics = s; continue; }
                    break;
            }

            BuildOutput(source, neighbor, edgeId);
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    private void BuildOutput(VertexId source, VertexId neighbor, EdgeId edgeId)
    {
        switch (_outputMode)
        {
            case ExpandOutputMode.NeighborOnly:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = neighbor.Value };
                break;
            case ExpandOutputMode.NeighborAndEdge:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.EdgeId, LongValue = edgeId.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = neighbor.Value };
                break;
            default:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = source.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.EdgeId, LongValue = edgeId.Value };
                _buffer[2] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = neighbor.Value };
                break;
        }
    }

    private bool TryMaterialize(VertexId id, out VertexId logical)
    {
        var materializer = new EntityIdentityMaterializer(_tx!.Vertices);
        return materializer.TryVertex(id, out logical);
    }

    public void Dispose()
    {
        _scanEnumerator?.Dispose();
        _scanEnumerator = null;
        _frontier = null;
        _source.Dispose();
    }
}
