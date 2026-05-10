using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using GraphDb.Engine;
using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;

namespace GraphDb.Benchmarks;

/// <summary>
/// BulkLoader vs. 通常 TX でのロード速度比較。
/// 目標: 1000万 edge を BulkLoader で 60秒以内
/// </summary>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 0, iterationCount: 1)]
[MemoryDiagnoser]
public class BulkLoadBenchmarks
{
    [Params(100_000, 1_000_000)]
    public int EdgeCount { get; set; }

    private string _bulkDbPath = null!;
    private string _txDbPath = null!;

    [IterationSetup]
    public void Setup()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        _bulkDbPath = Path.Combine(Path.GetTempPath(), $"quiver_bulk_{suffix}");
        _txDbPath   = Path.Combine(Path.GetTempPath(), $"quiver_tx_{suffix}");
    }

    [IterationCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_bulkDbPath)) Directory.Delete(_bulkDbPath, recursive: true);
        if (Directory.Exists(_txDbPath))   Directory.Delete(_txDbPath,   recursive: true);
    }

    [Benchmark(Description = "BulkLoader")]
    public void BulkLoad()
    {
        int nodeCount = Math.Max(EdgeCount / 10, 1_000);
        using var db = GraphDatabase.Open(_bulkDbPath);
        var labelId   = db.Schema.GetOrCreateLabel("Vertex");
        var relTypeId = db.Schema.GetOrCreateRelationshipType("KNOWS");

        using var bulk = db.BeginBulkLoad();

        for (long i = 0; i < nodeCount; i++)
            bulk.AppendNode(new NodeId(i), labelId);

        var rng = new Random(42);
        for (long i = 0; i < EdgeCount; i++)
        {
            long src = rng.Next(nodeCount);
            long tgt = rng.Next(nodeCount);
            bulk.AppendRelationship(new RelationshipId(i), new NodeId(src), new NodeId(tgt), relTypeId);
        }

        bulk.Commit();
    }

    [Benchmark(Baseline = true, Description = "Normal TX (batch 1000)")]
    public void TransactionLoad()
    {
        const int BatchSize = 1_000;
        int nodeCount = Math.Max(EdgeCount / 10, 1_000);
        using var db = GraphDatabase.Open(_txDbPath);

        var nodeIds = new NodeId[nodeCount];
        for (int i = 0; i < nodeCount; i += BatchSize)
        {
            using var tx = db.BeginTransaction();
            int end = Math.Min(i + BatchSize, nodeCount);
            for (int j = i; j < end; j++)
                nodeIds[j] = tx.CreateNode("Vertex");
            tx.Commit();
        }

        var rng = new Random(42);
        for (int i = 0; i < EdgeCount; i += BatchSize)
        {
            using var tx = db.BeginTransaction();
            int end = Math.Min(i + BatchSize, EdgeCount);
            for (int j = i; j < end; j++)
            {
                int src = rng.Next(nodeCount);
                int tgt = rng.Next(nodeCount);
                tx.CreateRelationship(nodeIds[src], nodeIds[tgt], "KNOWS");
            }
            tx.Commit();
        }
    }
}
