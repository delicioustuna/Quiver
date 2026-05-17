using Quiver.Client;

namespace Quiver.Client.Match;

/// <summary>
/// Match DSL のグラフパターン (ノード - エッジ - ノード) を表す不変オブジェクト。
/// <see cref="Node"/> をエントリポイントとし、<see cref="NodePattern.Out{TRel}(NodePattern)"/> や
/// <see cref="NodePattern.In{TRel}(NodePattern)"/> を連結してパターンを構築する。
/// </summary>
public sealed class GraphPattern
{
    internal NodePattern StartNode { get; }
    internal EdgePattern? Edge { get; }
    internal NodePattern? EndNode { get; }

    private GraphPattern(NodePattern start, EdgePattern? edge = null, NodePattern? end = null)
    {
        StartNode = start; Edge = edge; EndNode = end;
    }

    /// <summary>新しいノードパターンを開始する。<paramref name="variable"/> は <c>WHERE</c>/<c>RETURN</c> で参照する識別子。</summary>
    /// <param name="variable">パターン変数名 (例: <c>"n"</c>)。</param>
    /// <param name="label">マッチ対象のラベル (省略可)。</param>
    public static NodePattern Node(string variable, string? label = null)
        => new(variable, label, null);

    internal static GraphPattern From(NodePattern start, string edgeType, bool outgoing, NodePattern end)
        => new(start, new EdgePattern(edgeType, outgoing), end);
}

/// <summary>Match DSL におけるノードパターン。<see cref="Out(string, NodePattern)"/> 等でエッジを伸ばせる。</summary>
public sealed class NodePattern
{
    /// <summary>パターン変数名 (例: <c>"n"</c>)。</summary>
    public string Variable { get; }

    /// <summary>マッチ対象のラベル名 (未指定なら <c>null</c>)。</summary>
    public string? Label { get; }

    private readonly GraphPattern? _parent;

    internal NodePattern(string variable, string? label, GraphPattern? parent)
    {
        Variable = variable; Label = label; _parent = parent;
    }

    /// <summary>外向 (Outgoing) のエッジで <paramref name="end"/> ノードに連結する。</summary>
    public GraphPattern Out(string edgeType, NodePattern end)
        => GraphPattern.From(this, edgeType, outgoing: true, end);

    /// <summary>型付きの外向エッジで <paramref name="end"/> ノードに連結する。</summary>
    public GraphPattern Out<TRel>(NodePattern end) where TRel : IGraphRelationship<TRel>
        => GraphPattern.From(this, TRel.GraphType, outgoing: true, end);

    /// <summary>内向 (Incoming) のエッジで <paramref name="end"/> ノードに連結する。</summary>
    public GraphPattern In(string edgeType, NodePattern end)
        => GraphPattern.From(this, edgeType, outgoing: false, end);

    /// <summary>型付きの内向エッジで <paramref name="end"/> ノードに連結する。</summary>
    public GraphPattern In<TRel>(NodePattern end) where TRel : IGraphRelationship<TRel>
        => GraphPattern.From(this, TRel.GraphType, outgoing: false, end);
}

internal sealed class EdgePattern
{
    public string Type { get; }
    public bool Outgoing { get; }
    internal EdgePattern(string type, bool outgoing) { Type = type; Outgoing = outgoing; }
}
