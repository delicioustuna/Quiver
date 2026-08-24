using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// 隣接ブロックとlinked-listの1-hop比較。
/// Setup: BulkLoader で adjacency index を構築してから DB を再オープン。
/// Linked:        EnumerateEdges (linked-list)
/// ReadEdges:     IAdjacencySegmentStore.ReadEdges (連続メモリ・固定バッファ)
/// AdjCursor:     IAdjacencySegmentStore.OpenCursor ( page 継続 iterator)
///
/// degree 10_000 / 50_000 では ReadEdges の fixed-buffer fallback と
/// AdjCursor の差が顕著になる。AdjCursor は degree に依らず fallback しないこと。
/// </summary>
[MemoryDiagnoser]
public class OneHopAdjBenchmarks
{
    [Params(10, 100, 1_000, 10_000, 50_000)]
    public int Degree { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private VertexId _hub;
    private IReadTransaction _readTx = null!;
    private readonly AdjacencyEntry[] _adjBuf = new AdjacencyEntry[65_536];

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("1hop_adj");
        {
            using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            long hubId = 0;
            loader.AppendVertex(new VertexId(hubId), new LabelId(0));
            for (int i = 1; i <= Degree; i++)
            {
                loader.AppendVertex(new VertexId(i), new LabelId(1));
                loader.AppendEdge(new EdgeId(i - 1),
                    new VertexId(hubId), new VertexId(i), new EdgeTypeId(0));
            }
            loader.Commit();
        }
        // adj.db が書き出された状態で再オープン
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));
        _hub = new VertexId(0);
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

    [Benchmark(Description = "1-hop LinkedList", Baseline = true)]
    public int LinkedList()
    {
        int count = 0;
        var en = _readTx.EnumerateEdges(_hub, Direction.Outgoing);
        while (en.MoveNext()) count++;
        return count;
    }

    [Benchmark(Description = "1-hop AdjacencyBlock (ReadEdges)")]
    public int AdjacencyBlock()
    {
        var adj = _readTx.AsInternal().AdjacencySegments!;
        int count = adj.ReadEdges(_hub, Direction.Outgoing, null, _adjBuf);
        return count;
    }

    [Benchmark(Description = "1-hop AdjacencyCursor ()")]
    public int AdjacencyCursor()
    {
        var adj = _readTx.AsInternal().AdjacencySegments!;
        int count = 0;
        using var cursor = adj.OpenCursor(_hub, Direction.Outgoing, null);
        while (cursor.MoveNext()) count++;
        return count;
    }
}
