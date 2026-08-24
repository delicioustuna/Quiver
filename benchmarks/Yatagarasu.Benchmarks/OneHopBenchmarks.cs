using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// ハブVertexから全隣接Vertexを列挙する 1-hop スキャン。
/// 目標: degree=100 の場合 &lt; 1ms
/// </summary>
[MemoryDiagnoser]
public class OneHopBenchmarks
{
    [Params(10, 100, 1_000, 10_000)]
    public int Degree { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private VertexId _hub;
    private IReadTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("1hop");
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));

        using var tx = _db.BeginWriteTransaction();
        _hub = tx.CreateVertex("Hub");
        for (int i = 0; i < Degree; i++)
        {
            var leaf = tx.CreateVertex("Leaf");
            tx.CreateEdge(_hub, leaf, "EDGE");
        }
        tx.Commit();

        _readTx = _db.BeginWriteTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "1-hop OutgoingEdge scan")]
    public int OneHopScan()
    {
        int count = 0;
        var en = _readTx.EnumerateEdges(_hub, Direction.Outgoing);
        while (en.MoveNext()) count++;
        return count;
    }
}
