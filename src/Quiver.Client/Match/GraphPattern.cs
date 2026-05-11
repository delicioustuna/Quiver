using Quiver.Client;

namespace Quiver.Client.Match;

public sealed class GraphPattern
{
    internal NodePattern StartNode { get; }
    internal EdgePattern? Edge { get; }
    internal NodePattern? EndNode { get; }

    private GraphPattern(NodePattern start, EdgePattern? edge = null, NodePattern? end = null)
    {
        StartNode = start; Edge = edge; EndNode = end;
    }

    public static NodePattern Node(string variable, string? label = null)
        => new(variable, label, null);

    internal static GraphPattern From(NodePattern start, string edgeType, bool outgoing, NodePattern end)
        => new(start, new EdgePattern(edgeType, outgoing), end);
}

public sealed class NodePattern
{
    public string Variable { get; }
    public string? Label { get; }

    private readonly GraphPattern? _parent;

    internal NodePattern(string variable, string? label, GraphPattern? parent)
    {
        Variable = variable; Label = label; _parent = parent;
    }

    public GraphPattern Out(string edgeType, NodePattern end)
        => GraphPattern.From(this, edgeType, outgoing: true, end);

    public GraphPattern Out<TRel>(NodePattern end) where TRel : IGraphRelationship<TRel>
        => GraphPattern.From(this, TRel.GraphType, outgoing: true, end);

    public GraphPattern In(string edgeType, NodePattern end)
        => GraphPattern.From(this, edgeType, outgoing: false, end);

    public GraphPattern In<TRel>(NodePattern end) where TRel : IGraphRelationship<TRel>
        => GraphPattern.From(this, TRel.GraphType, outgoing: false, end);
}

internal sealed class EdgePattern
{
    public string Type { get; }
    public bool Outgoing { get; }
    internal EdgePattern(string type, bool outgoing) { Type = type; Outgoing = outgoing; }
}
