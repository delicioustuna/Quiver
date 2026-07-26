using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// <c>MergeVertex</c> (Cypher MERGE / Gremlin coalesce-fold-addV 相当) の
/// mixed read/write ループ性能を計測。
///
/// ループ: <c>g.MergeVertex("Person", "uid", uid)</c> を <see cref="Operations"/> 回呼び出す。
///   - <see cref="HitRatePercent"/> = 100 → 既存Vertexを毎回ヒット (ON MATCH パス)
///   - <see cref="HitRatePercent"/> = 0   → 毎回新規作成 (ON CREATE パス)
///   - 50 → 半々
///
/// ベースライン: 同じループを <c>CreateVertex + SetProperty</c> で書き直したもの (ヒット側だけ
/// no-op になる version で MergeVertex の look-up コストとの差分を見る)。
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

    /// <summary>
    /// <c>(label, uid)</c> にインデックスを登録するか。
    /// <c>true</c> のとき MergeVertex は O(log n) シーク経路、
    /// <c>false</c> のとき従来通り O(N) フルスキャン経路。
    /// </summary>
    [Params(false, true)]
    public bool WithIndex { get; set; }

    private const string IndexName = "idx_person_uid";

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("merge");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _ = _db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        _ = _db.EditSchema(schema => schema.GetOrCreatePropertyKey("uid"));
        if (WithIndex)
            _db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition(IndexName, new PropertyTarget(PropertyOwnerKind.Vertex, "uid", "Person"), IndexKind.Int64Equality)));

        using var tx = _db.BeginWriteTransaction();
        for (int i = 0; i < PreloadCount; i++)
        {
            var id = tx.CreateVertex("Person");
            tx.SetProperty(id, "uid", PropertyValue.FromInt64(i));
            if (WithIndex)
                tx.SetIndexedProperty(IndexName, i, id);
        }
        tx.Commit();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    private long PickUid(int i)
    {
        bool hit = (i * 100 / Operations) < HitRatePercent;
        if (hit)
            return i % PreloadCount; // 既存 uid を循環
        return PreloadCount + i;     // 新規 uid (PreloadCount 以降)
    }

    [Benchmark(Description = "MergeVertex loop (mixed read/write)")]
    public int MergeLoop()
    {
        int created = 0;
        using var tx = _db.BeginWriteTransaction();
        for (int i = 0; i < Operations; i++)
        {
            long uid = PickUid(i);
            var pv = PropertyValue.FromInt64(uid);
            var (_, c) = tx.MergeVertex("Person", "uid", in pv);
            if (c) created++;
        }
        // commit せず rollback (ベンチ毎の DB 肥大化を防ぐ)。
        // Dispose で自動 rollback。
        return created;
    }

    [Benchmark(Baseline = true, Description = "Baseline: explicit CreateVertex + SetProperty (no merge lookup)")]
    public int CreateOnlyLoop()
    {
        using var tx = _db.BeginWriteTransaction();
        for (int i = 0; i < Operations; i++)
        {
            long uid = PickUid(i);
            var id = tx.CreateVertex("Person");
            tx.SetProperty(id, "uid", PropertyValue.FromInt64(uid));
        }
        return Operations;
    }
}
