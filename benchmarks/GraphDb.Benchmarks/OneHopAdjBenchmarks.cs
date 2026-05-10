using BenchmarkDotNet.Attributes;
using GraphDb.Engine;
using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;

namespace GraphDb.Benchmarks;

/// <summary>
/// PW-4 隣接ブロック vs linked-list の 1-hop 比較。
/// Setup: BulkLoader で adjacency index を構築してから DB を再オープン。
/// Linked: EnumerateRelationships (linked-list)
/// Adj:    IAdjacencyBlockStore.ReadEdges (連続メモリ)
/// </summary>
[MemoryDiagnoser]
public class OneHopAdjBenchmarks
{
    [Params(10, 100, 1_000, 10_000)]
    public int Degree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId _hub;
    private IGraphTransaction _readTx = null!;
    private readonly AdjacencyEntry[] _adjBuf = new AdjacencyEntry[16_384];

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_1hop_adj_" + Guid.NewGuid().ToString("N")[..8]);
        {
            using var db = GraphDatabase.Open(_dbPath);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            long hubId = 0;
            loader.AppendNode(new NodeId(hubId), new LabelId(0));
            for (int i = 1; i <= Degree; i++)
            {
                loader.AppendNode(new NodeId(i), new LabelId(1));
                loader.AppendRelationship(new RelationshipId(i - 1),
                    new NodeId(hubId), new NodeId(i), new RelationshipTypeId(0));
            }
            loader.Commit();
        }
        // adj.db が書き出された状態で再オープン
        _db = GraphDatabase.Open(_dbPath);
        _hub = new NodeId(0);
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

    [Benchmark(Description = "1-hop LinkedList", Baseline = true)]
    public int LinkedList()
    {
        int count = 0;
        var en = _readTx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en.MoveNext()) count++;
        return count;
    }

    [Benchmark(Description = "1-hop AdjacencyBlock")]
    public int AdjacencyBlock()
    {
        var adj = _readTx.AdjacencyBlocks!;
        int count = adj.ReadEdges(_hub, Direction.Outgoing, null, _adjBuf);
        return count;
    }
}
