using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Client;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-18: フィルタ連鎖 + 展開 + sub-traversal + sort/limit を 1 本のクエリに乗せた複雑クエリ。
///
/// 形:
///   g.Nodes().HasLabel("Person")
///     .Has("city", "Tokyo")
///     .Out("KNOWS")
///     .Has("age", P.Gt(30))
///     .Where(t => t.Out("WORKS_AT").Has("type", "premium"))
///     .Order(descending: true)
///     .Limit(50)
///
/// ベースライン: フィルタ無しの全件展開 (Optimizer や述語評価のコストを差し引くため)。
/// 目的: optimizer や access-path 改修時に「フィルタ + 展開 + sort/limit」のトータルコストが
/// 回帰していないかを検知する。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FilterChainExpandBenchmarks
{
    [Params(10_000, 100_000)]
    public int NodeCount { get; set; }

    [Params(8, 32)]
    public int AvgDegree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;

    private static readonly string[] s_cities = ["Tokyo", "Osaka", "Kyoto", "Nagoya"];
    private static readonly string[] s_types  = ["premium", "standard", "trial"];

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_filterchain_" + Guid.NewGuid().ToString("N")[..8]);

        // Person ノード: city/age プロパティ付き
        // WORKS_AT ターゲット (Company): type プロパティ付き
        // KNOWS エッジ: Person 間で AvgDegree 本ずつ
        // WORKS_AT エッジ: Person → Company (各 Person 1 本)
        var rnd = new Random(42);
        using (var db = GraphDatabase.Open(_dbPath))
        {
            // schema をウォームアップ
            using (var schemaTx = db.BeginTransaction())
            {
                _ = db.Schema.GetOrCreateLabel("Person");
                _ = db.Schema.GetOrCreateLabel("Company");
                _ = db.Schema.GetOrCreatePropertyKey("city");
                _ = db.Schema.GetOrCreatePropertyKey("age");
                _ = db.Schema.GetOrCreatePropertyKey("type");
                _ = db.Schema.GetOrCreateRelationshipType("KNOWS");
                _ = db.Schema.GetOrCreateRelationshipType("WORKS_AT");
                schemaTx.Commit();
            }

            // ノード作成 (Person + 1/4 の Company)
            int companyCount = Math.Max(1, NodeCount / 4);
            var personIds = new NodeId[NodeCount];
            var companyIds = new NodeId[companyCount];
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < NodeCount; i++)
                {
                    var id = tx.CreateNode("Person");
                    personIds[i] = id;
                    tx.SetProperty(id, "city", PropertyValue.FromString(s_cities[i % s_cities.Length]));
                    tx.SetProperty(id, "age", PropertyValue.FromInt32(20 + (i % 50)));
                }
                for (int i = 0; i < companyCount; i++)
                {
                    var id = tx.CreateNode("Company");
                    companyIds[i] = id;
                    tx.SetProperty(id, "type", PropertyValue.FromString(s_types[i % s_types.Length]));
                }
                tx.Commit();
            }

            // エッジ作成 (バッチコミット)
            const int batchSize = 50_000;
            int created = 0;
            IGraphTransaction? batchTx = null;
            try
            {
                batchTx = db.BeginTransaction();
                for (int i = 0; i < NodeCount; i++)
                {
                    for (int d = 0; d < AvgDegree; d++)
                    {
                        int tgt = rnd.Next(NodeCount);
                        batchTx.CreateRelationship(personIds[i], personIds[tgt], "KNOWS");
                        created++;
                        if (created % batchSize == 0)
                        {
                            batchTx.Commit();
                            batchTx.Dispose();
                            batchTx = db.BeginTransaction();
                        }
                    }
                    int cIdx = rnd.Next(companyCount);
                    batchTx.CreateRelationship(personIds[i], companyIds[cIdx], "WORKS_AT");
                    created++;
                    if (created % batchSize == 0)
                    {
                        batchTx.Commit();
                        batchTx.Dispose();
                        batchTx = db.BeginTransaction();
                    }
                }
                batchTx.Commit();
            }
            finally
            {
                batchTx?.Dispose();
            }
        }

        _db = GraphDatabase.Open(_dbPath);
        _readTx = _db.BeginReadOnlyTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }

    [Benchmark(Baseline = true, Description = "Baseline: HasLabel scan + Out (no filter)")]
    public int BaselineExpandAll()
    {
        var g = _readTx.G(_db.Schema);
        return g.Nodes().HasLabel("Person").Out("KNOWS").ToList().Count;
    }

    [Benchmark(Description = "FilterChain: city+age+sub+order+limit")]
    public int FilterChainExpand()
    {
        var g = _readTx.G(_db.Schema);
        return g.Nodes().HasLabel("Person")
                    .Has("city", "Tokyo")
                    .Out("KNOWS")
                    .Has("age", P.Gt(30))
                    .Where(t => t.Out("WORKS_AT").Has("type", "premium"))
                    .Order(descending: true)
                    .Limit(50)
                    .ToList().Count;
    }

    [Benchmark(Description = "FilterChain (no sub-traversal)")]
    public int FilterChainNoSub()
    {
        var g = _readTx.G(_db.Schema);
        return g.Nodes().HasLabel("Person")
                    .Has("city", "Tokyo")
                    .Out("KNOWS")
                    .Has("age", P.Gt(30))
                    .Order(descending: true)
                    .Limit(50)
                    .ToList().Count;
    }
}
