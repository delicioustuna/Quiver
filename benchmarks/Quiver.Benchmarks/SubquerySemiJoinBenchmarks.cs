using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Client;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// Phase 2: SubquerySemiJoinPredicate を使う .Where() / .Not() の性能計測。
///
/// グラフ: NodeCount 個の "Person" ノードのうち半数が "Target" ノードへの "KNOWS" エッジを持つ。
///   - Where(t => t.Out("KNOWS"))  → KNOWS エッジあり半数のみ通過
///   - Not(t => t.Out("KNOWS"))   → KNOWS エッジなし半数のみ通過
/// ベースライン: .HasLabel("Person") の全件スキャン（フィルタなし）
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SubquerySemiJoinBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int NodeCount { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_ssj_" + Guid.NewGuid().ToString("N")[..8]);
        _db = GraphDatabase.Open(_dbPath);

        using var tx = _db.BeginTransaction();
        for (int i = 0; i < NodeCount; i++)
        {
            var person = tx.CreateNode("Person");
            if (i % 2 == 0)
            {
                var target = tx.CreateNode("Target");
                tx.CreateRelationship(person, target, "KNOWS");
            }
        }
        tx.Commit();

        _readTx = _db.BeginReadOnlyTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }

    [Benchmark(Baseline = true, Description = "V().HasLabel scan (no sub-traversal)")]
    public int BaselineScan()
    {
        var g = _readTx.G(_db.Schema);
        return g.Nodes().HasLabel("Person").ToList().Count;
    }

    [Benchmark(Description = "Where(t => t.Out(KNOWS)) EXISTS filter")]
    public int WhereOutExists()
    {
        var g = _readTx.G(_db.Schema);
        return g.Nodes().HasLabel("Person")
                    .Where(t => t.Out("KNOWS"))
                    .ToList().Count;
    }

    [Benchmark(Description = "Not(t => t.Out(KNOWS)) NOT EXISTS filter")]
    public int NotOutExists()
    {
        var g = _readTx.G(_db.Schema);
        return g.Nodes().HasLabel("Person")
                    .Not(t => t.Out("KNOWS"))
                    .ToList().Count;
    }
}
