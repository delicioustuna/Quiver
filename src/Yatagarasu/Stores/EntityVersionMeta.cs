namespace Yatagarasu.Storage.Records;

/// <summary>
/// entity slotの可視性とincarnationを表す24byteのsidecar recordです。
/// </summary>
internal readonly record struct EntityVersionMeta(
    long Xmin,
    long Xmax,
    long Generation = 0)
{
    internal const int Size = 24;
    internal static readonly EntityVersionMeta Unset = new(0, 0, 0);
    internal bool IsUnset => Xmin == 0 && Xmax == 0 && Generation == 0;
}
