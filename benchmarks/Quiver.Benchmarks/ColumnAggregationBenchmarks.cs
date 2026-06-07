using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// ARCH-5c Phase 5 (5g / TS-6): 列スキャン集約 vs row path 集約の回帰センチネル。
/// 全ノード full-scan の <c>SumLong(key)</c> を、(1) 列化済み (列スキャン) と (2) 列無し (row path =
/// per-node プロパティチェーン走査) で比較する。spike では projection が列で ~325× 高速だった。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ColumnAggregationBenchmarks
{
    [Params(10_000, 100_000)]
    public int NodeCount { get; set; }

    private GraphDatabase _colDb = null!;
    private GraphDatabase _rowDb = null!;
    private string _colPath = null!;
    private string _rowPath = null!;
    private IGraphTransaction _colTx = null!;
    private IGraphTransaction _rowTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _colPath = BenchTempDir.Create("colagg_col");
        _rowPath = BenchTempDir.Create("colagg_row");
        Seed(_colPath, withColumn: true);
        Seed(_rowPath, withColumn: false);
        _colDb = GraphDatabase.Open(System.IO.Path.Combine(_colPath, "graph.quiver"));
        _rowDb = GraphDatabase.Open(System.IO.Path.Combine(_rowPath, "graph.quiver"));
        _colTx = _colDb.BeginReadOnlyTransaction();
        _rowTx = _rowDb.BeginReadOnlyTransaction();
    }

    private void Seed(string dir, bool withColumn)
    {
        using var db = GraphDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
        _ = db.Schema.GetOrCreateLabel("N");
        const int batch = 50_000;
        int written = 0;
        IGraphTransaction? tx = db.BeginTransaction();
        try
        {
            for (int i = 0; i < NodeCount; i++)
            {
                var n = tx!.CreateNode("N");
                tx.SetProperty(n, "v", PropertyValue.FromInt64(i));
                if (++written >= batch)
                {
                    tx.Commit(); tx.Dispose(); tx = db.BeginTransaction(); written = 0;
                }
            }
            tx!.Commit();
        }
        finally { tx?.Dispose(); }

        if (withColumn) db.CreateColumn(EntityKind.Node, "v");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _colTx?.Dispose();
        _rowTx?.Dispose();
        _colDb?.Dispose();
        _rowDb?.Dispose();
        BenchTempDir.Delete(_colPath);
        BenchTempDir.Delete(_rowPath);
    }

    [Benchmark(Baseline = true, Description = "Row path SumLong (property chain walk)")]
    public long RowPathSum() => _rowTx.G(_rowDb.Schema).Nodes().SumLong("v");

    [Benchmark(Description = "Column scan SumLong (dense MVCC column)")]
    public long ColumnScanSum() => _colTx.G(_colDb.Schema).Nodes().SumLong("v");
}
