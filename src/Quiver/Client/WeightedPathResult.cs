using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// 重み付き最短経路 (<see cref="GraphTraversalSource.WeightedShortestPath(NodeId, NodeId, string, Quiver.Storage.Records.Direction, string?, double)"/> /
/// <see cref="GraphTraversalSource.WeightedShortestPathAStar"/>) の結果。
/// </summary>
/// <param name="Found">経路が見つかったか。</param>
/// <param name="Distance">
/// 経路上のエッジ重みの合計。<see cref="Found"/> が <c>false</c> のときは
/// <see cref="double.PositiveInfinity"/>。
/// </param>
/// <param name="Nodes">始点から終点までのノード列 (始点・終点を含む)。</param>
/// <param name="Relationships">
/// 経路上を順に辿るリレーションシップ列。要素数は <see cref="Nodes"/> の数 - 1。
/// </param>
public sealed record WeightedPathResult(
    bool Found,
    double Distance,
    IReadOnlyList<NodeId> Nodes,
    IReadOnlyList<RelationshipId> Relationships)
{
    /// <summary>経路が存在しないことを表す共有インスタンス。</summary>
    public static readonly WeightedPathResult NotFound =
        new(false, double.PositiveInfinity, [], []);
}

/// <summary>A* の座標ヒューリスティックで用いる距離尺度。</summary>
public enum HeuristicMetric
{
    /// <summary>2 次元ユークリッド距離 (平面座標)。エッジ重みも平面距離のとき admissible。</summary>
    Euclidean,

    /// <summary>
    /// 大圏距離 (緯度経度を度で受け取りメートルで返す)。エッジ重みもメートルのとき
    /// admissible (直線距離は実経路長以下のため)。
    /// </summary>
    Haversine,
}
