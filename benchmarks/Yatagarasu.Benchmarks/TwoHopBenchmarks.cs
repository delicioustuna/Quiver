using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// hub → level-1 × Degree → level-2 × Degree の 2-hop トラバーサル。
/// 目標: degree=10 (100 leaf) の場合 &lt; 1ms
/// </summary>
[MemoryDiagnoser]
public class TwoHopBenchmarks
{
    [Params(10, 50, 100)]
    public int Degree { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private VertexId _hub;
    private IReadTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("2hop");
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));

        const int BatchSize = 2_000;
        int totalMid = Degree;

        using (var tx = _db.BeginWriteTransaction())
        {
            _hub = tx.CreateVertex("Hub");
            tx.Commit();
        }

        // hub → mid vertices (可能なら1バッチで)
        var midVertices = new VertexId[totalMid];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < totalMid; i++)
            {
                midVertices[i] = tx.CreateVertex("Mid");
                tx.CreateEdge(_hub, midVertices[i], "EDGE");
            }
            tx.Commit();
        }

        // mid → leaf vertices (バッチ処理)
        int leafIdx = 0;
        int totalLeaf = totalMid * Degree;
        while (leafIdx < totalLeaf)
        {
            using var tx = _db.BeginWriteTransaction();
            int end = Math.Min(leafIdx + BatchSize, totalLeaf);
            while (leafIdx < end)
            {
                int midIdx = leafIdx / Degree;
                var leaf = tx.CreateVertex("Leaf");
                tx.CreateEdge(midVertices[midIdx], leaf, "EDGE");
                leafIdx++;
            }
            tx.Commit();
        }

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

    [Benchmark(Description = "2-hop traversal (Degree^2 leaves)")]
    public int TwoHopTraversal()
    {
        int count = 0;
        var en1 = _readTx.EnumerateEdges(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var mid = en1.Current.Target;
            var en2 = _readTx.EnumerateEdges(mid, Direction.Outgoing);
            while (en2.MoveNext()) count++;
        }
        return count;
    }
}
