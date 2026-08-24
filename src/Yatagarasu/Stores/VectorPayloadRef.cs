namespace Yatagarasu.Storage.Records;

/// <summary>immutable vector payload の物理 sequence と slot incarnation。</summary>
internal readonly record struct VectorPayloadRef(long Sequence, int Generation)
{
    public static VectorPayloadRef Invalid => new(-1, 0);

    public bool IsValid => Sequence >= 0 && Generation > 0;
}
