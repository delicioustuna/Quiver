using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-6: GraphStats.Collect() のコスト計測と QueryOptimizer の判定オーバーヘッド計測。
/// </summary>
[MemoryDiagnoser]
public class GraphStatsBenchmarks
{
    /// <summary>グラフのノード数（エッジは NodeCount * EdgeFactor）。</summary>
    [Params(1_000, 10_000, 100_000)]
    public int NodeCount { get; set; }

    private const int EdgeFactor = 5;   // 1ノードあたり平均5エッジ

    private GraphDatabase _db = null!;
    private string _dbPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("stats");

        {
            using var db = GraphDatabase.Open(_dbPath);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: false);

            // 3 labels: Person(60%), Car(30%), City(10%)
            long nodeId = 0;
            long relId  = 0;

            for (int i = 0; i < NodeCount; i++)
            {
                var label = i % 10 < 6 ? new LabelId(0)   // Person
                          : i % 10 < 9 ? new LabelId(1)   // Car
                                       : new LabelId(2);   // City
                loader.AppendNode(new NodeId(nodeId++), label);
            }

            // KNOWS edges between Person nodes
            int edgeCount = NodeCount * EdgeFactor;
            for (int i = 0; i < edgeCount; i++)
            {
                long src = i % NodeCount;
                long tgt = (i * 7 + 3) % NodeCount;       // pseudo-random pairing
                if (src == tgt) tgt = (tgt + 1) % NodeCount;
                loader.AppendRelationship(
                    new RelationshipId(relId++),
                    new NodeId(src), new NodeId(tgt),
                    new RelationshipTypeId(i % 2 == 0 ? 0 : 1)); // KNOWS / LIKES
            }
            loader.Commit();
        }

        _db = GraphDatabase.Open(_dbPath);
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
        return stats.TotalNodes;
    }
}

/// <summary>
/// PW-6: QueryOptimizer の判定ロジック単体のオーバーヘッド計測。
/// 実際の DB スキャンは含まない（stats は GlobalSetup 時に 1 回収集済み）。
/// </summary>
[MemoryDiagnoser]
public class QueryOptimizerBenchmarks
{
    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private QueryOptimizer _optimizer = null!;
    private LabelId _personLabel;
    private LabelId _carLabel;
    private RelationshipTypeId _knowsType;
    private RelationshipTypeId _likesType;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("opt");

        {
            using var db = GraphDatabase.Open(_dbPath);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: false);

            for (int i = 0; i < 10_000; i++)
            {
                var label = i % 10 < 6 ? new LabelId(0) : new LabelId(1);
                loader.AppendNode(new NodeId(i), label);
            }
            for (int i = 0; i < 50_000; i++)
            {
                long src = i % 10_000;
                long tgt = (i * 7 + 3) % 10_000;
                loader.AppendRelationship(
                    new RelationshipId(i), new NodeId(src), new NodeId(tgt),
                    new RelationshipTypeId(i % 2 == 0 ? 0 : 1));
            }
            loader.Commit();
        }

        _db = GraphDatabase.Open(_dbPath);
        _personLabel = new LabelId(0);
        _carLabel    = new LabelId(1);
        _knowsType   = new RelationshipTypeId(0);
        _likesType   = new RelationshipTypeId(1);

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
    [Benchmark(Baseline = true, Description = "SelectScan (LabelScan)")]
    public ScanKind SelectScanLabelOnly()
        => _optimizer.SelectScan(_personLabel).Kind;

    /// <summary>高選択率インデックス1候補 → IndexSeek を選択。</summary>
    [Benchmark(Description = "SelectScan (IndexSeek, 1 candidate)")]
    public ScanKind SelectScanWithSelectiveIndex()
    {
        var candidates = new[] { new IndexCandidate("name_idx", _personLabel, EstimatedRows: 5) };
        return _optimizer.SelectScan(_personLabel, candidates).Kind;
    }

    /// <summary>複数インデックス候補 → 最選択率のものを選択。</summary>
    [Benchmark(Description = "SelectScan (best of 3 candidates)")]
    public ScanKind SelectScanBestOfThree()
    {
        var candidates = new[]
        {
            new IndexCandidate("name_idx",  _personLabel, EstimatedRows: 200),
            new IndexCandidate("age_idx",   _personLabel, EstimatedRows: 50),
            new IndexCandidate("score_idx", _personLabel, EstimatedRows: 3),
        };
        return _optimizer.SelectScan(_personLabel, candidates).Kind;
    }

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
