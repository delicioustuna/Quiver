using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// B+Tree 索引が返す世代付きパック値 (<see cref="EntityRef"/>) を <c>NodeId.Value</c> へ
/// 解決する共通経路。slot が free→vacuum→再 Allocate で別ノードに再利用されている (ABA) 場合、
/// パック値の世代と「現在の slot 世代」(<see cref="INodeStore.CurrentGeneration"/>) が食い違うため
/// stale エントリを skip する。MVCC 可視性は適用しない (索引の従来挙動を保持し、世代不一致のみ弾く)。
/// </summary>
internal static class IndexValueResolver
{
    /// <summary>
    /// パック値が「現在生きている Node」を指すか (Kind が Node かつ slot 世代一致)。
    /// の WAND は top-k を確定する前に dead/再利用エントリを弾く必要があるため、
    /// スコアリングループ内でこの述語を使う (resolve 後 Take(k) と同じ可視性規約)。
    /// </summary>
    public static bool IsLiveNode(long packed, INodeStore nodes)
        => EntityRef.UnpackKind(packed) == EntityKind.Node
           && nodes.CurrentGeneration(EntityRef.UnpackSequence(packed)) == EntityRef.UnpackGeneration(packed);

    /// <summary>
    /// パック値の列挙を世代照合しつつ local packed <c>NodeId.Value</c> へ unpack する。
    /// Kind が Node でないエントリ、世代不一致エントリは除外する。
    /// </summary>
    public static IEnumerable<long> ResolveLiveNodeSequences(IEnumerable<long> packedValues, INodeStore nodes)
    {
        foreach (var packed in packedValues)
        {
            if (!IsLiveNode(packed, nodes)) continue;
            yield return EntityRef.PackLocal(
                EntityRef.UnpackSequence(packed),
                EntityRef.UnpackGeneration(packed));
        }
    }

    /// <summary>解決済み局所 ID 列挙を <see cref="NodeId"/> でラップする。</summary>
    public static IEnumerable<NodeId> ResolveLiveNodeIds(IEnumerable<long> packedValues, INodeStore nodes)
    {
        foreach (var value in ResolveLiveNodeSequences(packedValues, nodes))
            yield return new NodeId(value);
    }
}
