using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// hub → l1 × Degree → l2 × Degree → l3 × Degree の 3-hop トラバーサル。
/// Degree=5: 125 leaf, Degree=10: 1000 leaf
/// 目標: degree=5 の場合 &lt; 5ms
/// </summary>
[MemoryDiagnoser]
public class ThreeHopBenchmarks
{
    [Params(5, 10)]
    public int Degree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId _hub;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_3hop_" + Guid.NewGuid().ToString("N")[..8]);
        _db = GraphDatabase.Open(_dbPath);

        using (var tx = _db.BeginTransaction())
        {
            _hub = tx.CreateNode("Hub");
            tx.Commit();
        }

        var l1Nodes = CreateLevel(_hub, Degree, "L1");
        var l2Nodes = new NodeId[l1Nodes.Length * Degree];
        for (int i = 0; i < l1Nodes.Length; i++)
        {
            var sub = CreateLevel(l1Nodes[i], Degree, "L2");
            Array.Copy(sub, 0, l2Nodes, i * Degree, Degree);
        }
        for (int i = 0; i < l2Nodes.Length; i++)
            CreateLevel(l2Nodes[i], Degree, "L3");

        _readTx = _db.BeginTransaction();
    }

    private NodeId[] CreateLevel(NodeId parent, int count, string label)
    {
        var ids = new NodeId[count];
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < count; i++)
        {
            ids[i] = tx.CreateNode(label);
            tx.CreateRelationship(parent, ids[i], "EDGE");
        }
        tx.Commit();
        return ids;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }

    [Benchmark(Description = "3-hop traversal (Degree^3 leaves)")]
    public int ThreeHopTraversal()
    {
        int count = 0;
        var en1 = _readTx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var n1 = en1.Current.Target;
            var en2 = _readTx.EnumerateRelationships(n1, Direction.Outgoing);
            while (en2.MoveNext())
            {
                var n2 = en2.Current.Target;
                var en3 = _readTx.EnumerateRelationships(n2, Direction.Outgoing);
                while (en3.MoveNext()) count++;
            }
        }
        return count;
    }
}
