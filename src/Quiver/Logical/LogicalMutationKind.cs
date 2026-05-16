namespace Quiver.Logical;

/// <summary>
/// BA-7 / codex_advice_3 §8. Discriminator for <see cref="LogicalMutation"/>.
/// Mirrors the public graph-mutation surface of <see cref="IGraphTransaction"/>.
/// </summary>
public enum LogicalMutationKind : byte
{
    CreateNode = 1,
    DeleteNode = 2,
    CreateRelationship = 3,
    DeleteRelationship = 4,
    SetNodeProperty = 5,
    SetRelationshipProperty = 6,
    RemoveNodeProperty = 7,
}
