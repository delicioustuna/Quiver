namespace Quiver.Core;

/// <summary>
/// Tags an <see cref="EntityId"/> with the entity kind it refers to. Shared by
/// the vector store (VEC-1; codex_advice_3.md §6.2 — uses Node / Relationship)
/// and the internal tagged-id surface (FT-11; codex_advice_3.md §7.1 — adds
/// Property for diagnostics / catalog).
/// </summary>
public enum EntityKind : byte
{
    Node = 1,
    Relationship = 2,
    Property = 3,
}

/// <summary>
/// Internal tagged identifier that unifies <see cref="NodeId"/>, <see cref="RelationshipId"/>,
/// and <see cref="PropertyId"/> for diagnostics, operator plumbing, and future catalog use.
/// Not part of any on-disk format; introduce a format version byte before serializing.
/// </summary>
public readonly record struct EntityId(EntityKind Kind, long LocalId)
{
    public static readonly EntityId Invalid = new((EntityKind)0, -1);

    public bool IsValid => Kind != 0 && LocalId >= 0;

    public static EntityId FromNode(NodeId id) => new(EntityKind.Node, id.Value);
    public static EntityId FromRelationship(RelationshipId id) => new(EntityKind.Relationship, id.Value);
    public static EntityId FromProperty(PropertyId id) => new(EntityKind.Property, id.Value);

    public NodeId AsNode()
    {
        if (Kind != EntityKind.Node)
            throw new InvalidOperationException($"EntityId is {Kind}, not Node.");
        return new NodeId(LocalId);
    }

    public RelationshipId AsRelationship()
    {
        if (Kind != EntityKind.Relationship)
            throw new InvalidOperationException($"EntityId is {Kind}, not Relationship.");
        return new RelationshipId(LocalId);
    }

    public PropertyId AsProperty()
    {
        if (Kind != EntityKind.Property)
            throw new InvalidOperationException($"EntityId is {Kind}, not Property.");
        return new PropertyId(LocalId);
    }

    /// <summary>
    /// Pack into a 64-bit value: top 4 bits = <see cref="EntityKind"/>, low 60 bits = LocalId.
    /// LocalId must fit in 60 bits (0..2^60-1) or be -1 (Invalid). For in-memory diagnostics
    /// only; do not persist without a versioned format header.
    /// </summary>
    public ulong ToPacked()
    {
        if (!IsValid) return 0UL;
        if ((ulong)LocalId > (1UL << 60) - 1)
            throw new InvalidOperationException($"LocalId {LocalId} does not fit in 60 bits.");
        return ((ulong)(byte)Kind << 60) | (ulong)LocalId;
    }

    public static EntityId FromPacked(ulong packed)
    {
        if (packed == 0UL) return Invalid;
        var kind = (EntityKind)(byte)(packed >> 60);
        var local = (long)(packed & ((1UL << 60) - 1));
        return new EntityId(kind, local);
    }

    public override string ToString() => IsValid ? $"{Kind}#{LocalId}" : "Entity#Invalid";
}
