using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// Edge ID 列を、要求されたエンドポイント (source / target / other) の
/// Vertex ID 列に解決するオペレータ。<c>.OutEdges()</c> / <c>.InEdges()</c> /
/// <c>.BothEdges()</c> の後段で各端点を解決する。
/// </summary>
/// <remarks>
/// <see cref="EdgeEndpoint.Other"/> ではランタイムにどちら側から来たか不明なため、
/// 双方向走査パターンに合わせて <c>Target</c> を返す。既知のVertexに対する厳密な
/// "other" が必要な場合は <see cref="ExpandOperator"/> の出力モードを使う。
/// </remarks>
internal sealed class EdgeEndpointOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _edgeColumn;
    private readonly EdgeEndpoint _endpoint;
    private ITransaction? _tx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public EdgeEndpointOperator(IPhysicalOperator source, int edgeColumn, EdgeEndpoint endpoint)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _edgeColumn = edgeColumn;
        _endpoint = endpoint;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
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
        var edgeId = new EdgeId(_source.Current[_edgeColumn].LongValue);
        var edge = _tx!.Edges.Read(edgeId);
        long endpointId = _endpoint switch
        {
            EdgeEndpoint.Source => edge.Source.Value,
            EdgeEndpoint.Target => edge.Target.Value,
            // コンテキストVertexなしの "Other" は曖昧。OutE/BothE チェーンが通常
            // "source の反対側" を求めるため target をデフォルトにする。
            _ => edge.Target.Value,
        };
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = endpointId };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() => _source.Dispose();
}

internal enum EdgeEndpoint : byte
{
    Source = 1,
    Target = 2,
    Other = 3,
}
