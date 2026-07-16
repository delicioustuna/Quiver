using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// <see cref="IGraphSnapshotView"/> 上で動作するリファレンスグラフアルゴリズム群。
/// </summary>
/// <remarks>
/// スナップショット API の検証を主目的とし、最小割り当てではなく正しさ優先の実装。
/// </remarks>
public static class GraphAlgorithms
{
    /// <summary>
    /// スナップショットの出辺ビューで反復 PageRank を計算する。
    /// </summary>
    /// <remarks>
    /// 一様テレポートベクトルを用いた標準減衰定式化。dangling Vertex (出次数 0) は
    /// 各反復でランクを全Vertexに一様に再分配する。
    /// </remarks>
    /// <param name="view">読み取り対象のスナップショット。</param>
    /// <param name="damping">減衰係数 (典型値 0.85)。</param>
    /// <param name="iterations">固定小数点反復回数。</param>
    /// <returns>Vertex ID をインデックスとする <c>double[VertexCount]</c> のランク配列。</returns>
    public static double[] PageRank(IGraphSnapshotView view, double damping = 0.85, int iterations = 20)
    {
        long n = view.VertexCount;
        var rank = new double[n];
        var next = new double[n];
        if (n == 0) return rank;

        double initial = 1.0 / n;
        for (long i = 0; i < n; i++) rank[i] = initial;

        double teleport = (1.0 - damping) / n;

        for (int it = 0; it < iterations; it++)
        {
            double danglingMass = 0.0;
            for (long i = 0; i < n; i++)
            {
                int outDeg = view.OutDegree(new VertexId(i));
                if (outDeg == 0) danglingMass += rank[i];
            }
            double danglingShare = damping * danglingMass / n;

            for (long i = 0; i < n; i++)
                next[i] = teleport + danglingShare;

            for (long src = 0; src < n; src++)
            {
                int outDeg = view.OutDegree(new VertexId(src));
                if (outDeg == 0) continue;
                double share = damping * rank[src] / outDeg;
                ReadOnlySpan<long> neighbors = view.OutNeighbors(new VertexId(src));
                for (int k = 0; k < neighbors.Length; k++)
                    next[neighbors[k]] += share;
            }

            (rank, next) = (next, rank);
        }
        return rank;
    }
}
