namespace Quiver.Core;

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

internal readonly record struct PageId(long Value)
{
    public static readonly PageId Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct TransactionId(long Value)
{
    public static readonly TransactionId Invalid = new(-1);
    /// <summary>
    /// FT-26: MVCC コンテキスト未設定時 (bulk loader / recovery / 一部テスト) で xmin に書く既定値。
    /// 起動時に <c>CommittedTxRegistry</c> へ committed として登録され、全 snapshot から可視として扱われる。
    /// </summary>
    public static readonly TransactionId Bootstrap = new(1);
    public bool IsValid => Value >= 0;
}
