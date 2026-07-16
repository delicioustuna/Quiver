using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// Union / Coalesce / Optional の組み合わせコストを計測。
///
/// グラフ:
///   - Person Vertex: VertexCount 個
///   - KNOWS / FOLLOWS / MENTORS の 3 種のEdgeタイプ
///   - 各 Person からは KNOWS が AvgDegree 本、FOLLOWS が AvgDegree/2 本
///   - 一部の Person (1/3) には MENTORS エッジが存在 (Optional でヒット率を変える)
///
/// 計測対象: Union 3 分岐 / Coalesce 3 分岐 / Optional 1 分岐の dispatch + materialize コスト。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BranchedTraversalBenchmarks
{
    [Params(1_000, 10_000)]
    public int VertexCount { get; set; }

    [Params(8)]
    public int AvgDegree { get; set; }

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("branched");
        var rnd = new Random(7);
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver")))
        {
            _ = db.Schema.GetOrCreateLabel("Person");
            _ = db.Schema.GetOrCreateEdgeType("KNOWS");
            _ = db.Schema.GetOrCreateEdgeType("FOLLOWS");
            _ = db.Schema.GetOrCreateEdgeType("MENTORS");

            var ids = new VertexId[VertexCount];
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < VertexCount; i++) ids[i] = tx.CreateVertex("Person");
                tx.Commit();
            }

            const int batchSize = 50_000;
            int written = 0;
            IGraphTransaction? tx2 = null;
            try
            {
                tx2 = db.BeginTransaction();
                for (int i = 0; i < VertexCount; i++)
                {
                    for (int d = 0; d < AvgDegree; d++)
                    {
                        tx2.CreateEdge(ids[i], ids[rnd.Next(VertexCount)], "KNOWS");
                        written++;
                    }
                    int followCount = Math.Max(1, AvgDegree / 2);
                    for (int d = 0; d < followCount; d++)
                    {
                        tx2.CreateEdge(ids[i], ids[rnd.Next(VertexCount)], "FOLLOWS");
                        written++;
                    }
                    if (i % 3 == 0)
                    {
                        tx2.CreateEdge(ids[i], ids[rnd.Next(VertexCount)], "MENTORS");
                        written++;
                    }
                    if (written >= batchSize)
                    {
                        tx2.Commit();
                        tx2.Dispose();
                        tx2 = db.BeginTransaction();
                        written = 0;
                    }
                }
                tx2.Commit();
            }
            finally
            {
                tx2?.Dispose();
            }
        }

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _readTx = _db.BeginReadOnlyTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Baseline = true, Description = "Baseline: single Out(KNOWS)")]
    public int BaselineSingleExpand()
    {
        var g = _readTx.G(_db.Schema);
        return g.Vertices().HasLabel("Person").Out("KNOWS").ToList().Count;
    }

    [Benchmark(Description = "Union(KNOWS|FOLLOWS|MENTORS)")]
    public int Union3Branches()
    {
        var g = _readTx.G(_db.Schema);
        return g.Vertices().HasLabel("Person")
                    .Union(
                        t => t.Out("KNOWS"),
                        t => t.Out("FOLLOWS"),
                        t => t.Out("MENTORS"))
                    .ToList().Count;
    }

    [Benchmark(Description = "Coalesce(MENTORS|FOLLOWS|KNOWS)")]
    public int Coalesce3Branches()
    {
        var g = _readTx.G(_db.Schema);
        return g.Vertices().HasLabel("Person")
                    .Coalesce(
                        t => t.Out("MENTORS"),
                        t => t.Out("FOLLOWS"),
                        t => t.Out("KNOWS"))
                    .ToList().Count;
    }

    [Benchmark(Description = "Optional(MENTORS) — 1/3 hit rate")]
    public int OptionalMentors()
    {
        var g = _readTx.G(_db.Schema);
        return g.Vertices().HasLabel("Person")
                    .Optional(t => t.Out("MENTORS"))
                    .ToList().Count;
    }

    [Benchmark(Description = "Chained: Optional(MENTORS).Out(KNOWS)")]
    public int OptionalThenExpand()
    {
        var g = _readTx.G(_db.Schema);
        return g.Vertices().HasLabel("Person")
                    .Optional(t => t.Out("MENTORS"))
                    .Out("KNOWS")
                    .ToList().Count;
    }
}
