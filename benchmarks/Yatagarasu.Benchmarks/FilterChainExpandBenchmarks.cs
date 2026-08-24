using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// フィルタ連鎖 + 展開 + sub-traversal + sort/limit を 1 本のクエリに乗せた複雑クエリ。
///
/// 形:
///   g.Vertices().HasLabel("Person")
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
    public int VertexCount { get; set; }

    [Params(8, 32)]
    public int AvgDegree { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private IReadTransaction _readTx = null!;

    private static readonly string[] s_cities = ["Tokyo", "Osaka", "Kyoto", "Nagoya"];
    private static readonly string[] s_types  = ["premium", "standard", "trial"];

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("filterchain");

        // Person Vertex: city/age プロパティ付き
        // WORKS_AT ターゲット (Company): type プロパティ付き
        // KNOWS エッジ: Person 間で AvgDegree 本ずつ
        // WORKS_AT エッジ: Person → Company (各 Person 1 本)
        var rnd = new Random(42);
        using (var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata")))
        {
            // schema をウォームアップ
            using (var schemaTx = db.BeginWriteTransaction())
            {
                _ = db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
                _ = db.EditSchema(schema => schema.GetOrCreateLabel("Company"));
                _ = db.EditSchema(schema => schema.GetOrCreatePropertyKey("city"));
                _ = db.EditSchema(schema => schema.GetOrCreatePropertyKey("age"));
                _ = db.EditSchema(schema => schema.GetOrCreatePropertyKey("type"));
                _ = db.EditSchema(schema => schema.GetOrCreateEdgeType("KNOWS"));
                _ = db.EditSchema(schema => schema.GetOrCreateEdgeType("WORKS_AT"));
                schemaTx.Commit();
            }

            // Vertex作成 (Person + 1/4 の Company)
            int companyCount = Math.Max(1, VertexCount / 4);
            var personIds = new VertexId[VertexCount];
            var companyIds = new VertexId[companyCount];
            using (var tx = db.BeginWriteTransaction())
            {
                for (int i = 0; i < VertexCount; i++)
                {
                    var id = tx.CreateVertex("Person");
                    personIds[i] = id;
                    tx.SetProperty(id, "city", PropertyValue.FromString(s_cities[i % s_cities.Length]));
                    tx.SetProperty(id, "age", PropertyValue.FromInt32(20 + (i % 50)));
                }
                for (int i = 0; i < companyCount; i++)
                {
                    var id = tx.CreateVertex("Company");
                    companyIds[i] = id;
                    tx.SetProperty(id, "type", PropertyValue.FromString(s_types[i % s_types.Length]));
                }
                tx.Commit();
            }

            // エッジ作成 (バッチコミット)
            const int batchSize = 50_000;
            int created = 0;
            IWriteTransaction? batchTx = null;
            try
            {
                batchTx = db.BeginWriteTransaction();
                for (int i = 0; i < VertexCount; i++)
                {
                    for (int d = 0; d < AvgDegree; d++)
                    {
                        int tgt = rnd.Next(VertexCount);
                        batchTx.CreateEdge(personIds[i], personIds[tgt], "KNOWS");
                        created++;
                        if (created % batchSize == 0)
                        {
                            batchTx.Commit();
                            batchTx.Dispose();
                            batchTx = db.BeginWriteTransaction();
                        }
                    }
                    int cIdx = rnd.Next(companyCount);
                    batchTx.CreateEdge(personIds[i], companyIds[cIdx], "WORKS_AT");
                    created++;
                    if (created % batchSize == 0)
                    {
                        batchTx.Commit();
                        batchTx.Dispose();
                        batchTx = db.BeginWriteTransaction();
                    }
                }
                batchTx.Commit();
            }
            finally
            {
                batchTx?.Dispose();
            }
        }

        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));
        _readTx = _db.BeginReadTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Baseline = true, Description = "Baseline: HasLabel scan + Out (no filter)")]
    public int BaselineExpandAll()
    {
        var g = _readTx.Query;
        return g.Vertices().HasLabel("Person").Out("KNOWS").ToList().Count;
    }

    [Benchmark(Description = "FilterChain: city+age+sub+order+limit")]
    public int FilterChainExpand()
    {
        var g = _readTx.Query;
        return g.Vertices().HasLabel("Person")
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
        var g = _readTx.Query;
        return g.Vertices().HasLabel("Person")
                    .Has("city", "Tokyo")
                    .Out("KNOWS")
                    .Has("age", P.Gt(30))
                    .Order(descending: true)
                    .Limit(50)
                    .ToList().Count;
    }
}
