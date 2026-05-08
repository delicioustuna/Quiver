namespace GraphDb.Engine.Core;

public readonly record struct NodeId(long Value)
{
    public static readonly NodeId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct RelationshipId(long Value)
{
    public static readonly RelationshipId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct PropertyId(long Value)
{
    public static readonly PropertyId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct LabelId(int Value)
{
    public static readonly LabelId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct RelationshipTypeId(int Value)
{
    public static readonly RelationshipTypeId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct PropertyKeyId(int Value)
{
    public static readonly PropertyKeyId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct PageId(long Value)
{
    public static readonly PageId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct TransactionId(long Value)
{
    public static readonly TransactionId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}
