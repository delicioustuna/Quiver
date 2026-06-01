using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-4 / PW-8 隣接ブロック vs linked-list の 1-hop 比較。
/// Setup: BulkLoader で adjacency index を構築してから DB を再オープン。
/// Linked:        EnumerateRelationships (linked-list)
/// ReadEdges:     IAdjacencyBlockStore.ReadEdges (連続メモリ・固定バッファ)
/// AdjCursor:     IAdjacencyBlockStore.OpenCursor (PW-8 page 継続 iterator)
///
/// degree 10_000 / 50_000 では ReadEdges の fixed-buffer fallback と
/// AdjCursor の差が顕著になる。AdjCursor は degree に依らず fallback しないこと。
/// </summary>
[MemoryDiagnoser]
public class OneHopAdjBenchmarks
{
    [Params(10, 100, 1_000, 10_000, 50_000)]
    public int Degree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId _hub;
    private IGraphTransaction _readTx = null!;
    private readonly AdjacencyEntry[] _adjBuf = new AdjacencyEntry[65_536];

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("1hop_adj");
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
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "1-hop LinkedList", Baseline = true)]
    public int LinkedList()
    {
        int count = 0;
        var en = _readTx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en.MoveNext()) count++;
        return count;
    }

    [Benchmark(Description = "1-hop AdjacencyBlock (ReadEdges)")]
    public int AdjacencyBlock()
    {
        var adj = _readTx.AsInternal().AdjacencyBlocks!;
        int count = adj.ReadEdges(_hub, Direction.Outgoing, null, _adjBuf);
        return count;
    }

    [Benchmark(Description = "1-hop AdjacencyCursor (PW-8)")]
    public int AdjacencyCursor()
    {
        var adj = _readTx.AsInternal().AdjacencyBlocks!;
        int count = 0;
        using var cursor = adj.OpenCursor(_hub, Direction.Outgoing, null);
        while (cursor.MoveNext()) count++;
        return count;
    }
}
