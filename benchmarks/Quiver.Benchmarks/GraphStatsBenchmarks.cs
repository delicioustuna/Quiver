using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// GraphStats.Collect() のコスト計測と QueryOptimizer の判定オーバーヘッド計測。
/// </summary>
[MemoryDiagnoser]
public class GraphStatsBenchmarks
{
    /// <summary>グラフのVertex数（エッジは VertexCount * EdgeFactor）。</summary>
    [Params(1_000, 10_000, 100_000)]
    public int VertexCount { get; set; }

    private const int EdgeFactor = 5;   // 1Vertexあたり平均5エッジ

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("stats");

        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: false);

            // 3 labels: Person(60%), Car(30%), City(10%)
            long vertexId = 0;
            long edgeId  = 0;

            for (int i = 0; i < VertexCount; i++)
            {
                var label = i % 10 < 6 ? new LabelId(0)   // Person
                          : i % 10 < 9 ? new LabelId(1)   // Car
                                       : new LabelId(2);   // City
                loader.AppendVertex(new VertexId(vertexId++), label);
            }

            // KNOWS edges between Person vertices
            int edgeCount = VertexCount * EdgeFactor;
            for (int i = 0; i < edgeCount; i++)
            {
                long src = i % VertexCount;
                long tgt = (i * 7 + 3) % VertexCount;       // pseudo-random pairing
                if (src == tgt) tgt = (tgt + 1) % VertexCount;
                loader.AppendEdge(
                    new EdgeId(edgeId++),
                    new VertexId(src), new VertexId(tgt),
                    new EdgeTypeId(i % 2 == 0 ? 0 : 1)); // KNOWS / LIKES
            }
            loader.Commit();
        }

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    /// <summary>DB 全体をスキャンして統計を収集する。</summary>
    [Benchmark(Description = "GraphStats.Collect")]
    public long CollectStats()
    {
        var stats = _db.CollectStats();
        return stats.TotalVertices;
    }
}

/// <summary>
/// QueryOptimizer の判定ロジック単体のオーバーヘッド計測。
/// 実際の DB スキャンは含まない（stats は GlobalSetup 時に 1 回収集済み）。
/// </summary>
[MemoryDiagnoser]
public class QueryOptimizerBenchmarks
{
    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private QueryOptimizer _optimizer = null!;
    private LabelId _personLabel;
    private LabelId _carLabel;
    private EdgeTypeId _knowsType;
    private EdgeTypeId _likesType;
    private PropertyKeyId _nameKey;
    private PropertyKeyId _ageKey;
    private PropertyKeyId _scoreKey;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("opt");

        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: false);

            for (int i = 0; i < 10_000; i++)
            {
                var label = i % 10 < 6 ? new LabelId(0) : new LabelId(1);
                loader.AppendVertex(new VertexId(i), label);
            }
            for (int i = 0; i < 50_000; i++)
            {
                long src = i % 10_000;
                long tgt = (i * 7 + 3) % 10_000;
                loader.AppendEdge(
                    new EdgeId(i), new VertexId(src), new VertexId(tgt),
                    new EdgeTypeId(i % 2 == 0 ? 0 : 1));
            }
            loader.Commit();
        }

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _personLabel = new LabelId(0);
        _carLabel    = new LabelId(1);
        _knowsType   = new EdgeTypeId(0);
        _likesType   = new EdgeTypeId(1);
        _nameKey = new PropertyKeyId(0);
        _ageKey = new PropertyKeyId(1);
        _scoreKey = new PropertyKeyId(2);

        // 統計は GlobalSetup 時に 1 回だけ収集
        var stats = _db.CollectStats();
        _optimizer = _db.CreateOptimizer(stats);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    /// <summary>インデックスなし → LabelScan を選択。</summary>
    // ScanKind は internal 化したため戻り値は int に投影 (BDN の DCE 回避目的)。
    [Benchmark(Baseline = true, Description = "SelectScan (LabelScan)")]
    public int SelectScanLabelOnly()
        => (int)_optimizer.SelectScan(_personLabel).Kind;

    /// <summary>高選択率インデックス1候補 → IndexSeek を選択。</summary>
    [Benchmark(Description = "SelectScan (IndexSeek, 1 candidate)")]
    public int SelectScanWithSelectiveIndex()
    {
        var candidates = new[]
        {
            new IndexCandidate(
                VertexIndex("name_idx", "name"),
                _nameKey,
                _personLabel,
                EstimatedRows: 5),
        };
        return (int)_optimizer.SelectScan(_personLabel, candidates).Kind;
    }

    /// <summary>複数インデックス候補 → 最選択率のものを選択。</summary>
    [Benchmark(Description = "SelectScan (best of 3 candidates)")]
    public int SelectScanBestOfThree()
    {
        var candidates = new[]
        {
            new IndexCandidate(VertexIndex("name_idx", "name"), _nameKey, _personLabel, EstimatedRows: 200),
            new IndexCandidate(VertexIndex("age_idx", "age"), _ageKey, _personLabel, EstimatedRows: 50),
            new IndexCandidate(VertexIndex("score_idx", "score"), _scoreKey, _personLabel, EstimatedRows: 3),
        };
        return (int)_optimizer.SelectScan(_personLabel, candidates).Kind;
    }

    private static ScalarIndexDefinition VertexIndex(string name, string propertyKey)
        => new(
            name,
            new PropertyTarget(PropertyOwnerKind.Vertex, propertyKey, "Person"),
            IndexKind.Int64Equality);

    /// <summary>2ステップ traversal の並び替え。</summary>
    [Benchmark(Description = "OptimizeTraversal (2 steps)")]
    public int OptimizeTraversal2Steps()
    {
        var steps = new TraversalPlanStep[]
        {
            new(_knowsType, Direction.Outgoing),
            new(_likesType, Direction.Outgoing),
        };
        return _optimizer.OptimizeTraversal(steps).Count;
    }

    /// <summary>4ステップ traversal の並び替え。</summary>
    [Benchmark(Description = "OptimizeTraversal (4 steps)")]
    public int OptimizeTraversal4Steps()
    {
        var steps = new TraversalPlanStep[]
        {
            new(_knowsType, Direction.Outgoing),
            new(_likesType, Direction.Incoming),
            new(null,       Direction.Both),
            new(_knowsType, Direction.Incoming),
        };
        return _optimizer.OptimizeTraversal(steps).Count;
    }

    /// <summary>双方向展開推薦の判定。</summary>
    [Benchmark(Description = "ShouldUseBidirectional")]
    public bool BidirectionalCheck()
        => _optimizer.ShouldUseBidirectional(_personLabel, hopCount: 3);
}
