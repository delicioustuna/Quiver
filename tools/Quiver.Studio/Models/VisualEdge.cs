using Quiver.Core;

namespace Quiver.Studio.Models;

public sealed class VisualEdge
{
    public RelationshipId Id { get; }
    public VisualNode Source { get; }
    public VisualNode Target { get; }
    public string RelationshipType { get; }

    public VisualEdge(RelationshipId id, VisualNode source, VisualNode target, string relationshipType)
    {
        Id = id;
        Source = source;
        Target = target;
        RelationshipType = relationshipType;
    }
}
