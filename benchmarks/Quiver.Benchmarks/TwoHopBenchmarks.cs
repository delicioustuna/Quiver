using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// hub → level-1 × Degree → level-2 × Degree の 2-hop トラバーサル。
/// 目標: degree=10 (100 leaf) の場合 &lt; 1ms
/// </summary>
[MemoryDiagnoser]
public class TwoHopBenchmarks
{
    [Params(10, 50, 100)]
    public int Degree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId _hub;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("2hop");
        _db = GraphDatabase.Open(_dbPath);

        const int BatchSize = 2_000;
        int totalMid = Degree;

        using (var tx = _db.BeginTransaction())
        {
            _hub = tx.CreateNode("Hub");
            tx.Commit();
        }

        // hub → mid nodes (可能なら1バッチで)
        var midNodes = new NodeId[totalMid];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < totalMid; i++)
            {
                midNodes[i] = tx.CreateNode("Mid");
                tx.CreateRelationship(_hub, midNodes[i], "EDGE");
            }
            tx.Commit();
        }

        // mid → leaf nodes (バッチ処理)
        int leafIdx = 0;
        int totalLeaf = totalMid * Degree;
        while (leafIdx < totalLeaf)
        {
            using var tx = _db.BeginTransaction();
            int end = Math.Min(leafIdx + BatchSize, totalLeaf);
            while (leafIdx < end)
            {
                int midIdx = leafIdx / Degree;
                var leaf = tx.CreateNode("Leaf");
                tx.CreateRelationship(midNodes[midIdx], leaf, "EDGE");
                leafIdx++;
            }
            tx.Commit();
        }

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

    [Benchmark(Description = "2-hop traversal (Degree^2 leaves)")]
    public int TwoHopTraversal()
    {
        int count = 0;
        var en1 = _readTx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var mid = en1.Current.Target;
            var en2 = _readTx.EnumerateRelationships(mid, Direction.Outgoing);
            while (en2.MoveNext()) count++;
        }
        return count;
    }
}
