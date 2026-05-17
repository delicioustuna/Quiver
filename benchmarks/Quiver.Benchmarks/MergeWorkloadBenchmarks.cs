using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Client;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-18: GC-5 で導入された <c>MergeNode</c> (Cypher MERGE / Gremlin coalesce-fold-addV 相当) の
/// mixed read/write ループ性能を計測。
///
/// ループ: <c>g.MergeNode("Person", "uid", uid)</c> を <see cref="Operations"/> 回呼び出す。
///   - <see cref="HitRatePercent"/> = 100 → 既存ノードを毎回ヒット (ON MATCH パス)
///   - <see cref="HitRatePercent"/> = 0   → 毎回新規作成 (ON CREATE パス)
///   - 50 → 半々
///
/// ベースライン: 同じループを <c>CreateNode + SetProperty</c> で書き直したもの (ヒット側だけ
/// no-op になる version で MergeNode の look-up コストとの差分を見る)。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class MergeWorkloadBenchmarks
{
    [Params(0, 50, 100)]
    public int HitRatePercent { get; set; }

    [Params(500)]
    public int Operations { get; set; }

    [Params(10_000)]
    public int PreloadCount { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_merge_" + Guid.NewGuid().ToString("N")[..8]);
        _db = GraphDatabase.Open(_dbPath);
        _ = _db.Schema.GetOrCreateLabel("Person");
        _ = _db.Schema.GetOrCreatePropertyKey("uid");

        using var tx = _db.BeginTransaction();
        for (int i = 0; i < PreloadCount; i++)
        {
            var id = tx.CreateNode("Person");
            tx.SetProperty(id, "uid", PropertyValue.FromInt64(i));
        }
        tx.Commit();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }

    private long PickUid(int i)
    {
        bool hit = (i * 100 / Operations) < HitRatePercent;
        if (hit)
            return i % PreloadCount; // 既存 uid を循環
        return PreloadCount + i;     // 新規 uid (PreloadCount 以降)
    }

    [Benchmark(Description = "MergeNode loop (mixed read/write)")]
    public int MergeLoop()
    {
        int created = 0;
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < Operations; i++)
        {
            long uid = PickUid(i);
            var pv = PropertyValue.FromInt64(uid);
            var (_, c) = tx.MergeNode("Person", "uid", in pv);
            if (c) created++;
        }
        // commit せず rollback (ベンチ毎の DB 肥大化を防ぐ)。
        // Dispose で自動 rollback。
        return created;
    }

    [Benchmark(Baseline = true, Description = "Baseline: explicit CreateNode + SetProperty (no merge lookup)")]
    public int CreateOnlyLoop()
    {
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < Operations; i++)
        {
            long uid = PickUid(i);
            var id = tx.CreateNode("Person");
            tx.SetProperty(id, "uid", PropertyValue.FromInt64(uid));
        }
        return Operations;
    }
}
