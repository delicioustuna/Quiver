using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// B+Tree 索引が返す世代付きパック値 (<see cref="EntityRef"/>) を <c>VertexId.Value</c> へ
/// 解決する共通経路。slot が free→vacuum→再 Allocate で別Vertexに再利用されている (ABA) 場合、
/// パック値の世代と「現在の slot 世代」(<see cref="IVertexStore.CurrentGeneration"/>) が食い違うため
/// stale エントリを skip する。MVCC 可視性は適用しない (索引の従来挙動を保持し、世代不一致のみ弾く)。
/// </summary>
internal static class IndexValueResolver
{
    /// <summary>
    /// パック値が「現在生きている Vertex」を指すか (Kind が Vertex かつ slot 世代一致)。
    /// の WAND は top-k を確定する前に dead/再利用エントリを弾く必要があるため、
    /// スコアリングループ内でこの述語を使う (resolve 後 Take(k) と同じ可視性規約)。
    /// </summary>
    public static bool IsLiveVertex(long packed, IVertexStore vertices)
        => EntityRef.UnpackKind(packed) == EntityKind.Vertex
           && vertices.CurrentGeneration(EntityRef.UnpackSequence(packed)) == EntityRef.UnpackGeneration(packed);

    /// <summary>
    /// パック値の列挙を世代照合しつつ local packed <c>VertexId.Value</c> へ unpack する。
    /// Kind が Vertex でないエントリ、世代不一致エントリは除外する。
    /// </summary>
    public static IEnumerable<long> ResolveLiveVertexSequences(IEnumerable<long> packedValues, IVertexStore vertices)
    {
        foreach (var packed in packedValues)
        {
            if (!IsLiveVertex(packed, vertices)) continue;
            yield return EntityRef.PackLocal(
                EntityRef.UnpackSequence(packed),
                EntityRef.UnpackGeneration(packed));
        }
    }

    /// <summary>解決済み局所 ID 列挙を <see cref="VertexId"/> でラップする。</summary>
    public static IEnumerable<VertexId> ResolveLiveVertexIds(IEnumerable<long> packedValues, IVertexStore vertices)
    {
        foreach (var value in ResolveLiveVertexSequences(packedValues, vertices))
            yield return new VertexId(value);
    }
}
