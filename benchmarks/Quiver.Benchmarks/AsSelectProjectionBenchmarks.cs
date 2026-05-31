using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-18: GC-6 で導入された <c>.As("a") / .Select("a") / .Select&lt;T&gt;(...)</c> の
/// carry-column overhead を計測。
///
/// 形:
///   g.Nodes().HasLabel("Person").As("a")
///     .Out("KNOWS").As("b")
///     .Out("KNOWS").As("c")
///     .Select&lt;(NodeId,NodeId,NodeId)&gt;(t => (t.Node("a"), t.Node("b"), t.Node("c")))
///
/// ベースライン: 同形のチェーンを <c>.As</c> 無しで実行 (最終 .Count() のみ)。
/// 計測対象: alias を維持するための tuple slot 拡張・projection lambda の overhead。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class AsSelectProjectionBenchmarks
{
    [Params(1_000, 10_000)]
    public int NodeCount { get; set; }

    [Params(4)]
    public int AvgDegree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("assel");
        var rnd = new Random(11);
        using (var db = GraphDatabase.Open(_dbPath))
        {
            _ = db.Schema.GetOrCreateLabel("Person");
            _ = db.Schema.GetOrCreateRelationshipType("KNOWS");
            var ids = new NodeId[NodeCount];
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < NodeCount; i++) ids[i] = tx.CreateNode("Person");
                tx.Commit();
            }
            const int batchSize = 50_000;
            int written = 0;
            IGraphTransaction? tx2 = null;
            try
            {
                tx2 = db.BeginTransaction();
                for (int i = 0; i < NodeCount; i++)
                {
                    for (int d = 0; d < AvgDegree; d++)
                    {
                        tx2.CreateRelationship(ids[i], ids[rnd.Next(NodeCount)], "KNOWS");
                        written++;
                        if (written >= batchSize)
                        {
                            tx2.Commit();
                            tx2.Dispose();
                            tx2 = db.BeginTransaction();
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

        _db = GraphDatabase.Open(_dbPath);
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

    [Benchmark(Baseline = true, Description = "Baseline: chain without As/Select")]
    public int BaselineNoAlias()
    {
        var g = _readTx.G(_db.Schema);
        return g.Nodes().HasLabel("Person")
                    .Out("KNOWS")
                    .Out("KNOWS")
                    .ToList().Count;
    }

    [Benchmark(Description = "As(a).Out.As(b): alias maintained, terminal Count")]
    public int AsAliasCount()
    {
        var g = _readTx.G(_db.Schema);
        return (int)g.Nodes().HasLabel("Person").As("a")
                    .Out("KNOWS").As("b")
                    .Out("KNOWS").As("c")
                    .Count();
    }

    [Benchmark(Description = "Select<(a,b,c)>: tuple materialize per row")]
    public int SelectTuple3()
    {
        var g = _readTx.G(_db.Schema);
        var result = g.Nodes().HasLabel("Person").As("a")
                    .Out("KNOWS").As("b")
                    .Out("KNOWS").As("c")
                    .Select(t => (t.Node("a"), t.Node("b"), t.Node("c")));
        return result.Count;
    }

    [Benchmark(Description = "Select(\"a\"): pin then continue traversal")]
    public int SelectPinContinue()
    {
        var g = _readTx.G(_db.Schema);
        return g.Nodes().HasLabel("Person").As("a")
                    .Out("KNOWS")
                    .Out("KNOWS")
                    .Select("a")
                    .ToList().Count;
    }
}
