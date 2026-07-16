using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// Phase 2: SubquerySemiJoinPredicate を使う .Where() / .Not() の性能計測。
///
/// グラフ: VertexCount 個の "Person" Vertexのうち半数が "Target" Vertexへの "KNOWS" エッジを持つ。
///   - Where(t => t.Out("KNOWS"))  → KNOWS エッジあり半数のみ通過
///   - Not(t => t.Out("KNOWS"))   → KNOWS エッジなし半数のみ通過
/// ベースライン: .HasLabel("Person") の全件スキャン（フィルタなし）
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SubquerySemiJoinBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int VertexCount { get; set; }

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("ssj");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        for (int i = 0; i < VertexCount; i++)
        {
            var person = tx.CreateVertex("Person");
            if (i % 2 == 0)
            {
                var target = tx.CreateVertex("Target");
                tx.CreateEdge(person, target, "KNOWS");
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
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Baseline = true, Description = "V().HasLabel scan (no sub-traversal)")]
    public int BaselineScan()
    {
        var g = _readTx.G(_db.Schema);
        return g.Vertices().HasLabel("Person").ToList().Count;
    }

    [Benchmark(Description = "Where(t => t.Out(KNOWS)) EXISTS filter")]
    public int WhereOutExists()
    {
        var g = _readTx.G(_db.Schema);
        return g.Vertices().HasLabel("Person")
                    .Where(t => t.Out("KNOWS"))
                    .ToList().Count;
    }

    [Benchmark(Description = "Not(t => t.Out(KNOWS)) NOT EXISTS filter")]
    public int NotOutExists()
    {
        var g = _readTx.G(_db.Schema);
        return g.Vertices().HasLabel("Person")
                    .Not(t => t.Out("KNOWS"))
                    .ToList().Count;
    }
}
