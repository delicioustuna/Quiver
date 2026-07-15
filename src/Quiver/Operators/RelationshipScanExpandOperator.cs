using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// リレーションシップストアを逐次スキャンし、事前構築した <see cref="FrontierSet"/> で
/// ソースノードに突合する 1 ホップ展開オペレータ。frontier がエッジ総数に対して大きい場合に
/// ノードごとのリンクリスト / 隣接ブロック走査より有利になる — N 本の独立リストを辿ると
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
internal sealed class RelationshipScanExpandOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly Direction _direction;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly ExpandOutputMode _outputMode;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;

    private ITransaction? _tx;
    private FrontierSet? _frontier;
    private IEnumerator<RelationshipId>? _scanEnumerator;

    public RelationshipScanExpandOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        ExpandOutputMode outputMode)
    {
        _source = source;
        _sourceNodeColumn = sourceNodeColumn;
        _direction = direction;
        _typeFilter = typeFilter;
        _outputMode = outputMode;
        (_buffer, _schema) = outputMode switch
        {
            ExpandOutputMode.NeighborOnly => (new TupleSlot[1], new TupleSchema([
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
            ExpandOutputMode.NeighborAndRel => (new TupleSlot[2], new TupleSchema([
                new ColumnDefinition("rel", TupleSlotType.RelationshipId),
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
            _ => (new TupleSlot[3], new TupleSchema([
                new ColumnDefinition("source", TupleSlotType.NodeId),
                new ColumnDefinition("rel", TupleSlotType.RelationshipId),
                new ColumnDefinition("neighbor", TupleSlotType.NodeId)])),
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
        var ids = new List<NodeId>(64);
        long max = -1;
        while (_source.MoveNext())
        {
            if (!TryMaterialize(new NodeId(_source.Current[_sourceNodeColumn].LongValue), out var id))
                continue;
            // Why not Sequence-only: vacuum 後に再利用された slot を seed と誤って突合すると、
            // stale frontier が現在の別ノードの relationship を展開してしまう。
            ids.Add(id);
            if (id.Sequence > max) max = id.Sequence;
        }
        _frontier = FrontierSet.Build(ids, max);
        _scanEnumerator = tx.Relationships.Scan().GetEnumerator();
    }

    public bool MoveNext()
    {
        if (_scanEnumerator == null || _frontier == null) return false;
        while (_scanEnumerator.MoveNext())
        {
            var relId = _scanEnumerator.Current;
            var rel = _tx!.Relationships.Read(relId);

            if (_typeFilter.HasValue && rel.Type != _typeFilter.Value) continue;

            // スキャンが検査した全リレーションシップを論理読み取りとして計上する。
            // ノードごとのパスの RowsProduced と比較してスキャン局所性の効果を評価できる。
            var s = Statistics;
            s.RelationshipScanRecords++;

            if (!TryMaterialize(rel.Source, out NodeId relSource)
                || !TryMaterialize(rel.Target, out NodeId relTarget))
                continue;
            NodeId source, neighbor;
            switch (_direction)
            {
                case Direction.Outgoing:
                    if (!_frontier.Contains(relSource)) { Statistics = s; continue; }
                    source = relSource; neighbor = relTarget;
                    break;
                case Direction.Incoming:
                    if (!_frontier.Contains(relTarget)) { Statistics = s; continue; }
                    source = relTarget; neighbor = relSource;
                    break;
                default: // Both
                    if (_frontier.Contains(relSource))
                    {
                        source = relSource; neighbor = relTarget;
                    }
                    else if (_frontier.Contains(relTarget))
                    {
                        source = relTarget; neighbor = relSource;
                    }
                    else { Statistics = s; continue; }
                    break;
            }

            BuildOutput(source, neighbor, relId);
            s.RowsProduced++;
            Statistics = s;
            return true;
        }
        return false;
    }

    private void BuildOutput(NodeId source, NodeId neighbor, RelationshipId relId)
    {
        switch (_outputMode)
        {
            case ExpandOutputMode.NeighborOnly:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
            case ExpandOutputMode.NeighborAndRel:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = relId.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
            default:
                _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = source.Value };
                _buffer[1] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = relId.Value };
                _buffer[2] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = neighbor.Value };
                break;
        }
    }

    private bool TryMaterialize(NodeId id, out NodeId logical)
    {
        var materializer = new EntityIdentityMaterializer(_tx!.Nodes);
        return materializer.TryNode(id, out logical);
    }

    public void Dispose()
    {
        _scanEnumerator?.Dispose();
        _scanEnumerator = null;
        _frontier = null;
        _source.Dispose();
    }
}
