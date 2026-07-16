using Quiver.Core;

namespace Quiver.Studio.Models;

public sealed class VisualEdge
{
    public EdgeId Id { get; }
    public VisualVertex Source { get; }
    public VisualVertex Target { get; }
    public string EdgeType { get; }

    public bool IsSelected { get; set; }

    public VisualEdge(EdgeId id, VisualVertex source, VisualVertex target, string edgeType)
    {
        Id = id;
        Source = source;
        Target = target;
        EdgeType = edgeType;
    }
}
