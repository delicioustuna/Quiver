using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// 列スキャン集約とrow path集約の回帰センチネル。
/// 全Vertex full-scan の <c>SumLong(key)</c> を、(1) 列化済み (列スキャン) と (2) 列無し (row path =
/// per-vertex プロパティチェーン走査) で比較する。spike では projection が列で ~325× 高速だった。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ColumnAggregationBenchmarks
{
    [Params(10_000, 100_000)]
    public int VertexCount { get; set; }

    private QuiverDatabase _colDb = null!;
    private QuiverDatabase _rowDb = null!;
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
        _colDb = QuiverDatabase.Open(System.IO.Path.Combine(_colPath, "graph.quiver"));
        _rowDb = QuiverDatabase.Open(System.IO.Path.Combine(_rowPath, "graph.quiver"));
        _colTx = _colDb.BeginReadOnlyTransaction();
        _rowTx = _rowDb.BeginReadOnlyTransaction();
    }

    private void Seed(string dir, bool withColumn)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
        _ = db.Schema.GetOrCreateLabel("N");
        const int batch = 50_000;
        int written = 0;
        IGraphTransaction? tx = db.BeginTransaction();
        try
        {
            for (int i = 0; i < VertexCount; i++)
            {
                var n = tx!.CreateVertex("N");
                tx.SetProperty(n, "v", PropertyValue.FromInt64(i));
                if (++written >= batch)
                {
                    tx.Commit(); tx.Dispose(); tx = db.BeginTransaction(); written = 0;
                }
            }
            tx!.Commit();
        }
        finally { tx?.Dispose(); }

        if (withColumn) db.CreateColumn(EntityKind.Vertex, "v");
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
    public long RowPathSum() => _rowTx.G(_rowDb.Schema).Vertices().SumLong("v");

    [Benchmark(Description = "Column scan SumLong (dense MVCC column)")]
    public long ColumnScanSum() => _colTx.G(_colDb.Schema).Vertices().SumLong("v");
}
