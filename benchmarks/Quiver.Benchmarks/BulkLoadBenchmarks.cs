using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// BulkLoader vs. 通常 TX でのロード速度比較。
/// 目標: 1000万 edge を BulkLoader で 60秒以内
/// </summary>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 0, iterationCount: 1)]
[MemoryDiagnoser]
public class BulkLoadBenchmarks
{
    // PW-9: 10M case is the streaming target. The TX baseline is skipped at 10M
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
        int nodeCount = Math.Max(EdgeCount / 10, 1_000);
        using var db = GraphDatabase.Open(_bulkDbPath);
        var labelId   = db.Schema.GetOrCreateLabel("Vertex");
        var relTypeId = db.Schema.GetOrCreateRelationshipType("KNOWS");

        using var bulk = db.BeginBulkLoad();

        for (long i = 0; i < nodeCount; i++)
            bulk.AppendNode(new NodeId(i), labelId);

        var rng = new Random(42);
        for (long i = 0; i < EdgeCount; i++)
        {
            long src = rng.Next(nodeCount);
            long tgt = rng.Next(nodeCount);
            bulk.AppendRelationship(new RelationshipId(i), new NodeId(src), new NodeId(tgt), relTypeId);
        }

        bulk.Commit();
    }

    /// <summary>PW-9: temp-file streamed variant, target for 10M+ edge imports.</summary>
    [Benchmark(Description = "StreamingBulkLoader")]
    public void StreamingBulkLoad()
    {
        int nodeCount = Math.Max(EdgeCount / 10, 1_000);
        using var db = GraphDatabase.Open(_streamingDbPath);
        var labelId   = db.Schema.GetOrCreateLabel("Vertex");
        var relTypeId = db.Schema.GetOrCreateRelationshipType("KNOWS");

        using var bulk = db.BeginStreamingBulkLoad();

        for (long i = 0; i < nodeCount; i++)
            bulk.AppendNode(new NodeId(i), labelId);

        var rng = new Random(42);
        for (long i = 0; i < EdgeCount; i++)
        {
            long src = rng.Next(nodeCount);
            long tgt = rng.Next(nodeCount);
            bulk.AppendRelationship(new RelationshipId(i), new NodeId(src), new NodeId(tgt), relTypeId);
        }

        bulk.Commit();
    }

    [Benchmark(Baseline = true, Description = "Normal TX (batch 1000)")]
    public void TransactionLoad()
    {
        if (EdgeCount >= 10_000_000) return; // skip — TX path is hours at this size
        const int BatchSize = 1_000;
        int nodeCount = Math.Max(EdgeCount / 10, 1_000);
        using var db = GraphDatabase.Open(_txDbPath);

        var nodeIds = new NodeId[nodeCount];
        for (int i = 0; i < nodeCount; i += BatchSize)
        {
            using var tx = db.BeginTransaction();
            int end = Math.Min(i + BatchSize, nodeCount);
            for (int j = i; j < end; j++)
                nodeIds[j] = tx.CreateNode("Vertex");
            tx.Commit();
        }

        var rng = new Random(42);
        for (int i = 0; i < EdgeCount; i += BatchSize)
        {
            using var tx = db.BeginTransaction();
            int end = Math.Min(i + BatchSize, EdgeCount);
            for (int j = i; j < end; j++)
            {
                int src = rng.Next(nodeCount);
                int tgt = rng.Next(nodeCount);
                tx.CreateRelationship(nodeIds[src], nodeIds[tgt], "KNOWS");
            }
            tx.Commit();
        }
    }
}
