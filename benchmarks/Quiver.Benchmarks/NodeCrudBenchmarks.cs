using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// 目標: 点検索 warm cache &lt; 5µs、Insert &lt; 20µs
/// </summary>
[MemoryDiagnoser]
public class NodeCrudBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int NodeCount { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId[] _nodeIds = null!;
    private IGraphTransaction _readTx = null!;
    private readonly Random _rng = new(42);

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("crud");
        _db = GraphDatabase.Open(_dbPath);
        _nodeIds = new NodeId[NodeCount];

        const int BatchSize = 5_000;
        for (int i = 0; i < NodeCount; i += BatchSize)
        {
            using var tx = _db.BeginTransaction();
            int end = Math.Min(i + BatchSize, NodeCount);
            for (int j = i; j < end; j++)
                _nodeIds[j] = tx.CreateNode("Vertex");
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

    [Benchmark(Description = "NodeExists (warm)")]
    public bool LookupNode()
    {
        var id = _nodeIds[_rng.Next(_nodeIds.Length)];
        return _readTx.NodeExists(id);
    }

    [Benchmark(Description = "CreateNode + Commit")]
    public NodeId InsertNode()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("Vertex");
        tx.Commit();
        return id;
    }

    [Benchmark(Description = "CreateNode + SetProperty + Commit")]
    public NodeId InsertNodeWithProperty()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.CreateNode("Vertex");
        tx.SetProperty(id, "id", PropertyValue.FromInt64(id.Value));
        tx.Commit();
        return id;
    }
}
