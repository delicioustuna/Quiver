namespace Yatagarasu.Api;

/// <summary>
/// <see cref="MatchTuple.TypeOf"/> が返すスロット値型。内部物理 enum
/// (<c>Yatagarasu.Query.Physical.TupleSlotType</c>) を公開 API 向けに写像した安定 enum。
/// </summary>
internal enum MatchValueType : byte
{
    /// <summary>NULL / 未バインド。</summary>
    Null = 0,

    /// <summary><see cref="Yatagarasu.Core.VertexId"/>。</summary>
    Vertex = 1,

    /// <summary><see cref="Yatagarasu.Core.EdgeId"/>。</summary>
    Edge = 2,

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

    /// <summary><see cref="Yatagarasu.Core.NexusId"/>。</summary>
    Nexus = 8,
}
