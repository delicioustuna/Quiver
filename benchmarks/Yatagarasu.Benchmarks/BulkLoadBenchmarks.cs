using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// BulkLoader vs. 通常 TX でのロード速度比較。
/// 目標: 1000万 edge を BulkLoader で 60秒以内
/// </summary>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 0, iterationCount: 1)]
[MemoryDiagnoser]
public class BulkLoadBenchmarks
{
    // 10M case is the streaming target. The TX baseline is skipped at 10M
    // (would take >30 min); only BulkLoader / StreamingBulkLoader are measured there.
    [Params(100_000, 1_000_000, 10_000_000)]
    public int EdgeCount { get; set; }

    private string _bulkDbPath = null!;
    private string _streamingDbPath = null!;
    private string _txDbPath = null!;

    [IterationSetup]
    public void Setup()
    {
        // このベンチは 10M edge × 3 DB を作るため 1 DB が GB 級になる。
        // [IterationSetup] は反復ごとに走り、そのたびに新しいランダム名で
        // ディレクトリを作るので、kill されると数 GB 単位で残骸が漏れる。
        // BenchTempDir 経由にして単一ルート配下に集め、起動時 SweepRoot で
        // 確実に回収できるようにする(原因詳細は BenchTempDir 参照)。
        _bulkDbPath      = BenchTempDir.Create("bulk");
        _streamingDbPath = BenchTempDir.Create("streaming");
        _txDbPath        = BenchTempDir.Create("tx");
    }

    [IterationCleanup]
    public void Cleanup()
    {
        // 旧実装は Directory.Delete を try/catch 無しで 3 行ベタ書きしていた。
        // 1 つ目の削除が IOException(mmap ハンドルの解放遅延など)を投げると
        // 残り 2 つが実行されず確実に漏れ、さらに cleanup の未捕捉例外が BDN
        // の run 自体を中断させていた。BenchTempDir.Delete はリトライ付きで
        // 例外を握り潰すため、3 つすべてが必ず削除を試行される。
        BenchTempDir.Delete(_bulkDbPath);
        BenchTempDir.Delete(_streamingDbPath);
        BenchTempDir.Delete(_txDbPath);
    }

    [Benchmark(Description = "BulkLoader")]
    public void BulkLoad()
    {
        int vertexCount = Math.Max(EdgeCount / 10, 1_000);
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_bulkDbPath, "graph.yata"));
        var labelId   = db.EditSchema(schema => schema.GetOrCreateLabel("Vertex"));
        var edgeTypeId = db.EditSchema(schema => schema.GetOrCreateEdgeType("KNOWS"));

        using var bulk = db.BeginBulkLoad();

        for (long i = 0; i < vertexCount; i++)
            bulk.AppendVertex(new VertexId(i), labelId);

        var rng = new Random(42);
        for (long i = 0; i < EdgeCount; i++)
        {
            long src = rng.Next(vertexCount);
            long tgt = rng.Next(vertexCount);
            bulk.AppendEdge(new EdgeId(i), new VertexId(src), new VertexId(tgt), edgeTypeId);
        }

        bulk.Commit();
    }

    /// <summary>: temp-file streamed variant, target for 10M+ edge imports.</summary>
    [Benchmark(Description = "StreamingBulkLoader")]
    public void StreamingBulkLoad()
    {
        int vertexCount = Math.Max(EdgeCount / 10, 1_000);
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_streamingDbPath, "graph.yata"));
        var labelId   = db.EditSchema(schema => schema.GetOrCreateLabel("Vertex"));
        var edgeTypeId = db.EditSchema(schema => schema.GetOrCreateEdgeType("KNOWS"));

        using var bulk = db.BeginStreamingBulkLoad();

        for (long i = 0; i < vertexCount; i++)
            bulk.AppendVertex(new VertexId(i), labelId);

        var rng = new Random(42);
        for (long i = 0; i < EdgeCount; i++)
        {
            long src = rng.Next(vertexCount);
            long tgt = rng.Next(vertexCount);
            bulk.AppendEdge(new EdgeId(i), new VertexId(src), new VertexId(tgt), edgeTypeId);
        }

        bulk.Commit();
    }

    [Benchmark(Baseline = true, Description = "Normal TX (batch 1000)")]
    public void TransactionLoad()
    {
        if (EdgeCount >= 10_000_000) return; // skip — TX path is hours at this size
        const int BatchSize = 1_000;
        int vertexCount = Math.Max(EdgeCount / 10, 1_000);
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_txDbPath, "graph.yata"));

        var vertexIds = new VertexId[vertexCount];
        for (int i = 0; i < vertexCount; i += BatchSize)
        {
            using var tx = db.BeginWriteTransaction();
            int end = Math.Min(i + BatchSize, vertexCount);
            for (int j = i; j < end; j++)
                vertexIds[j] = tx.CreateVertex("Vertex");
            tx.Commit();
        }

        var rng = new Random(42);
        for (int i = 0; i < EdgeCount; i += BatchSize)
        {
            using var tx = db.BeginWriteTransaction();
            int end = Math.Min(i + BatchSize, EdgeCount);
            for (int j = i; j < end; j++)
            {
                int src = rng.Next(vertexCount);
                int tgt = rng.Next(vertexCount);
                tx.CreateEdge(vertexIds[src], vertexIds[tgt], "KNOWS");
            }
            tx.Commit();
        }
    }
}
