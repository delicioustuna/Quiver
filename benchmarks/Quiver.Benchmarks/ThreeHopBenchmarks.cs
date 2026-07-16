using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
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

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private VertexId _hub;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("3hop");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));

        using (var tx = _db.BeginTransaction())
        {
            _hub = tx.CreateVertex("Hub");
            tx.Commit();
        }

        var l1Vertices = CreateLevel(_hub, Degree, "L1");
        var l2Vertices = new VertexId[l1Vertices.Length * Degree];
        for (int i = 0; i < l1Vertices.Length; i++)
        {
            var sub = CreateLevel(l1Vertices[i], Degree, "L2");
            Array.Copy(sub, 0, l2Vertices, i * Degree, Degree);
        }
        for (int i = 0; i < l2Vertices.Length; i++)
            CreateLevel(l2Vertices[i], Degree, "L3");

        _readTx = _db.BeginTransaction();
    }

    private VertexId[] CreateLevel(VertexId parent, int count, string label)
    {
        var ids = new VertexId[count];
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < count; i++)
        {
            ids[i] = tx.CreateVertex(label);
            tx.CreateEdge(parent, ids[i], "EDGE");
        }
        tx.Commit();
        return ids;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "3-hop traversal (Degree^3 leaves)")]
    public int ThreeHopTraversal()
    {
        int count = 0;
        var en1 = _readTx.EnumerateEdges(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var n1 = en1.Current.Target;
            var en2 = _readTx.EnumerateEdges(n1, Direction.Outgoing);
            while (en2.MoveNext())
            {
                var n2 = en2.Current.Target;
                var en3 = _readTx.EnumerateEdges(n2, Direction.Outgoing);
                while (en3.MoveNext()) count++;
            }
        }
        return count;
    }
}
