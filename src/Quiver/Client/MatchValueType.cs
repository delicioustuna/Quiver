namespace Quiver.Api;

/// <summary>
/// <see cref="MatchTuple.TypeOf"/> が返すスロット値型。内部物理 enum
/// (<c>Quiver.Query.Physical.TupleSlotType</c>) を公開 API 向けに写像した安定 enum。
/// </summary>
public enum MatchValueType : byte
{
    /// <summary>NULL / 未バインド。</summary>
    Null = 0,

    /// <summary><see cref="Quiver.Core.NodeId"/>。</summary>
    Node = 1,

    /// <summary><see cref="Quiver.Core.RelationshipId"/>。</summary>
    Relationship = 2,

    /// <summary><see cref="bool"/>。</summary>
    Boolean = 3,

    /// <summary><see cref="long"/>。</summary>
    Int64 = 4,

    /// <summary><see cref="double"/>。</summary>
    Double = 5,

    /// <summary>UTF-8 文字列。</summary>
    String = 6,

    /// <summary>バイト列。</summary>
    Bytes = 7,
}
