using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// PW-15. Reference graph-algorithm kernels that operate against an
/// <see cref="IGraphSnapshotView"/>. These exist to validate the snapshot
/// API surface — they intentionally do the simplest correct thing rather
/// than chasing the lowest-allocation variant.
/// </summary>
public static class GraphAlgorithms
{
    /// <summary>
    /// Iterative PageRank over the snapshot's outgoing-edge view. Uses the
    /// standard damping formulation with a uniform teleport vector. Dangling
    /// nodes redistribute their rank uniformly to all nodes each iteration.
    /// </summary>
    /// <param name="view">Snapshot to read from.</param>
    /// <param name="damping">Damping factor, typically 0.85.</param>
    /// <param name="iterations">Number of fixed-point iterations.</param>
    /// <returns>A <c>double[NodeCount]</c> with one rank per node id.</returns>
    public static double[] PageRank(IGraphSnapshotView view, double damping = 0.85, int iterations = 20)
    {
        long n = view.NodeCount;
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
                int outDeg = view.OutDegree(new NodeId(i));
                if (outDeg == 0) danglingMass += rank[i];
            }
            double danglingShare = damping * danglingMass / n;

            for (long i = 0; i < n; i++)
                next[i] = teleport + danglingShare;

            for (long src = 0; src < n; src++)
            {
                int outDeg = view.OutDegree(new NodeId(src));
                if (outDeg == 0) continue;
                double share = damping * rank[src] / outDeg;
                ReadOnlySpan<long> neighbors = view.OutNeighbors(new NodeId(src));
                for (int k = 0; k < neighbors.Length; k++)
                    next[neighbors[k]] += share;
            }

            (rank, next) = (next, rank);
        }
        return rank;
    }
}
