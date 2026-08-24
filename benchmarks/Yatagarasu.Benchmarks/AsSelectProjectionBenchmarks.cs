using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// <c>.As("a") / .Select("a") / .Select&lt;T&gt;(...)</c> の
/// carry-column overhead を計測。
///
/// 形:
///   g.Vertices().HasLabel("Person").As("a")
///     .Out("KNOWS").As("b")
///     .Out("KNOWS").As("c")
///     .Select&lt;(VertexId,VertexId,VertexId)&gt;(t => (t.Vertex("a"), t.Vertex("b"), t.Vertex("c")))
///
/// ベースライン: 同形のチェーンを <c>.As</c> 無しで実行 (最終 .Count() のみ)。
/// 計測対象: alias を維持するための tuple slot 拡張・projection lambda の overhead。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class AsSelectProjectionBenchmarks
{
    [Params(1_000, 10_000)]
    public int VertexCount { get; set; }

    [Params(4)]
    public int AvgDegree { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private IReadTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("assel");
        var rnd = new Random(11);
        using (var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata")))
        {
            _ = db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
            _ = db.EditSchema(schema => schema.GetOrCreateEdgeType("KNOWS"));
            var ids = new VertexId[VertexCount];
            using (var tx = db.BeginWriteTransaction())
            {
                for (int i = 0; i < VertexCount; i++) ids[i] = tx.CreateVertex("Person");
                tx.Commit();
            }
            const int batchSize = 50_000;
            int written = 0;
            IWriteTransaction? tx2 = null;
            try
            {
                tx2 = db.BeginWriteTransaction();
                for (int i = 0; i < VertexCount; i++)
                {
                    for (int d = 0; d < AvgDegree; d++)
                    {
                        tx2.CreateEdge(ids[i], ids[rnd.Next(VertexCount)], "KNOWS");
                        written++;
                        if (written >= batchSize)
                        {
                            tx2.Commit();
                            tx2.Dispose();
                            tx2 = db.BeginWriteTransaction();
                            written = 0;
                        }
                    }
                }
                tx2.Commit();
            }
            finally
            {
                tx2?.Dispose();
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

    [Benchmark(Baseline = true, Description = "Baseline: chain without As/Select")]
    public int BaselineNoAlias()
    {
        var g = _readTx.Query;
        return g.Vertices().HasLabel("Person")
                    .Out("KNOWS")
                    .Out("KNOWS")
                    .ToList().Count;
    }

    [Benchmark(Description = "As(a).Out.As(b): alias maintained, terminal Count")]
    public int AsAliasCount()
    {
        var g = _readTx.Query;
        return (int)g.Vertices().HasLabel("Person").As("a")
                    .Out("KNOWS").As("b")
                    .Out("KNOWS").As("c")
                    .Count();
    }

    [Benchmark(Description = "Select<(a,b,c)>: tuple materialize per row")]
    public int SelectTuple3()
    {
        var g = _readTx.Query;
        var result = g.Vertices().HasLabel("Person").As("a")
                    .Out("KNOWS").As("b")
                    .Out("KNOWS").As("c")
                    .Select(t => (t.Vertex("a"), t.Vertex("b"), t.Vertex("c")));
        return result.Count;
    }

    [Benchmark(Description = "Select(\"a\"): pin then continue traversal")]
    public int SelectPinContinue()
    {
        var g = _readTx.Query;
        return g.Vertices().HasLabel("Person").As("a")
                    .Out("KNOWS")
                    .Out("KNOWS")
                    .Select("a")
                    .ToList().Count;
    }
}
