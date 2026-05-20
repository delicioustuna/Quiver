using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-15 / codex_advice_3 §7.7. Compare per-pass cost of running PageRank
/// against three neighbour sources for the same graph:
/// - <c>AdjacencyCursor</c>: open + walk a fresh <see cref="AdjacencyCursor"/>
///   for every node every iteration (the previous "best" path).
/// - <c>SnapshotView</c>: build a CSR/CSC <see cref="GraphSnapshotView"/> once
///   and iterate flat <see cref="ReadOnlySpan{T}"/> rows each pass.
///
/// The snapshot pays an O(N + E) build cost on first use; for repeated
/// algorithms the per-pass span access should beat per-call cursor open +
/// page pin allocation. <c>MemoryDiagnoser</c> shows the allocation gap.
/// </summary>
[MemoryDiagnoser]
public class SnapshotViewBenchmarks
{
    [Params(1_000, 10_000)]
    public int NodeCount { get; set; }

    [Params(8)]
    public int AvgDegree { get; set; }

    [Params(10)]
    public int Iterations { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("snap");

        var rng = new Random(42);
        {
            using var db = GraphDatabase.Open(_dbPath);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            for (int i = 0; i < NodeCount; i++)
                loader.AppendNode(new NodeId(i), new LabelId(0));

            int relId = 0;
            for (int i = 0; i < NodeCount; i++)
            {
                for (int e = 0; e < AvgDegree; e++)
                {
                    int target = rng.Next(NodeCount);
                    if (target == i) continue;
                    loader.AppendRelationship(
                        new RelationshipId(relId++),
                        new NodeId(i), new NodeId(target),
                        new RelationshipTypeId(0));
                }
            }
            loader.Commit();
        }
        _db = GraphDatabase.Open(_dbPath);
        _readTx = _db.BeginTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "PageRank via AdjacencyCursor (per-pass open)", Baseline = true)]
    public double PageRank_Cursor()
    {
        var adj = _readTx.AdjacencyBlocks!;
        int n = NodeCount;
        var rank = new double[n];
        var next = new double[n];
        for (int i = 0; i < n; i++) rank[i] = 1.0 / n;

        for (int it = 0; it < Iterations; it++)
        {
            for (int i = 0; i < n; i++) next[i] = (1.0 - 0.85) / n;
            for (int src = 0; src < n; src++)
            {
                using var c = adj.OpenCursor(new NodeId(src), Direction.Outgoing, null);
                int outDeg = 0;
                var neighbors = new List<long>();
                while (c.MoveNext())
                {
                    if (adj.IsTombstoned(c.Relationship)) continue;
                    neighbors.Add(c.Neighbor.Value);
                    outDeg++;
                }
                if (outDeg == 0) continue;
                double share = 0.85 * rank[src] / outDeg;
                for (int k = 0; k < neighbors.Count; k++)
                    next[neighbors[k]] += share;
            }
            (rank, next) = (next, rank);
        }
        return rank[0];
    }

    [Benchmark(Description = "PageRank via SnapshotView (CSR span)")]
    public double PageRank_Snapshot()
    {
        using var view = _db.OpenSnapshotView();
        var ranks = GraphAlgorithms.PageRank(view, damping: 0.85, iterations: Iterations);
        return ranks[0];
    }
}
