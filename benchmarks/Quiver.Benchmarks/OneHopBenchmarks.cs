using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// ハブノードから全隣接ノードを列挙する 1-hop スキャン。
/// 目標: degree=100 の場合 &lt; 1ms
/// </summary>
[MemoryDiagnoser]
public class OneHopBenchmarks
{
    [Params(10, 100, 1_000, 10_000)]
    public int Degree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId _hub;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_1hop_" + Guid.NewGuid().ToString("N")[..8]);
        _db = GraphDatabase.Open(_dbPath);

        using var tx = _db.BeginTransaction();
        _hub = tx.CreateNode("Hub");
        for (int i = 0; i < Degree; i++)
        {
            var leaf = tx.CreateNode("Leaf");
            tx.CreateRelationship(_hub, leaf, "EDGE");
        }
        tx.Commit();

        _readTx = _db.BeginTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }

    [Benchmark(Description = "1-hop OutgoingEdge scan")]
    public int OneHopScan()
    {
        int count = 0;
        var en = _readTx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en.MoveNext()) count++;
        return count;
    }
}
