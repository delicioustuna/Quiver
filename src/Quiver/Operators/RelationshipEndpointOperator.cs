using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// リレーションシップ ID 列を、要求されたエンドポイント (source / target / other) の
/// ノード ID 列に解決するオペレータ。<c>.OutRelationships()</c> / <c>.InRelationships()</c> /
/// <c>.BothRelationships()</c> の後段で Gremlin の <c>.outV()</c> / <c>.inV()</c> /
/// <c>.otherV()</c> ステップを実装する。
/// </summary>
/// <remarks>
/// <see cref="RelationshipEndpoint.Other"/> ではランタイムにどちら側から来たか不明なため、
/// BothE 走査パターンに合わせて <c>Target</c> を返す。既知のノードに対する厳密な
/// "other" が必要な場合は <see cref="ExpandOperator"/> の出力モードを使う。
/// </remarks>
internal sealed class RelationshipEndpointOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _relColumn;
    private readonly RelationshipEndpoint _endpoint;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public RelationshipEndpointOperator(IPhysicalOperator source, int relColumn, RelationshipEndpoint endpoint)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _relColumn = relColumn;
        _endpoint = endpoint;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
    }

    public bool MoveNext()
    {
        if (!_source.MoveNext()) return false;
        var relId = new RelationshipId(_source.Current[_relColumn].LongValue);
        var rel = _tx!.Relationships.Read(relId);
        long endpointId = _endpoint switch
        {
            RelationshipEndpoint.Source => rel.Source.Value,
            RelationshipEndpoint.Target => rel.Target.Value,
            // コンテキストノードなしの "Other" は曖昧。OutE/BothE チェーンが通常
            // "source の反対側" を求めるため target をデフォルトにする。
            _ => rel.Target.Value,
        };
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = endpointId };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() => _source.Dispose();
}

internal enum RelationshipEndpoint : byte
{
    Source = 1,
    Target = 2,
    Other = 3,
}
