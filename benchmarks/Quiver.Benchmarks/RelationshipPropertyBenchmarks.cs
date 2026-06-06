using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// ARCH-5c Phase 4/6: relationship property の inline 経路を計測する。
/// <list type="bullet">
///   <item><c>LookupRelProperty</c> — 単一 rel の inline property read (版チェーン walk 1 段)。</item>
///   <item><c>InsertRelWithProperty</c> — rel 作成 + scalar property + commit。</item>
///   <item><c>ProjectionScanWeights</c> — 全 rel の weight を読む projection スキャン。
///     これが Phase 5 (列指向) の対象ワークロード — 版チェーン walk を列セグメント逐次走査に
///     置換できれば高速化する。本ベンチの値が Phase 5 着手要否の実測根拠になる。</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class RelationshipPropertyBenchmarks
{
    [Params(10_000, 100_000)]
    public int RelCount { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private RelationshipId[] _relIds = null!;
    private IGraphTransaction _readTx = null!;
    private NodeId _hub;
    private Quiver.Storage.Records.IRelationshipPropertyJoinIndex _weightColumn = null!;
    private readonly Random _rng = new(42);

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("relprop");
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _relIds = new RelationshipId[RelCount];

        const int BatchSize = 5_000;
        // hub 1 個 + leaf を都度作って hub→leaf エッジに weight を inline で持たせる。
        using (var seed = _db.BeginTransaction())
        {
            _hub = seed.CreateNode("Hub");
            seed.Commit();
        }

        for (int i = 0; i < RelCount; i += BatchSize)
        {
            using var tx = _db.BeginTransaction();
            int end = Math.Min(i + BatchSize, RelCount);
            for (int j = i; j < end; j++)
            {
                var leaf = tx.CreateNode("Leaf");
                var rel = tx.CreateRelationship(_hub, leaf, "LINK");
                tx.SetProperty(rel, "weight", PropertyValue.FromInt64(j));
                _relIds[j] = rel;
            }
            tx.Commit();
        }

        _readTx = _db.BeginTransaction();

        // Phase 5 列指向 proto: dense Sequence→value 列セグメント (join index) を 1 回構築。
        // projection はこれを逐次走査でき per-rel page pin / chain walk を回避する。
        _weightColumn = _db.BuildRelationshipPropertyJoinIndex("weight", PropertyValueType.Int64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "GetProperty(rel) inline (warm)")]
    public long LookupRelProperty()
    {
        var id = _relIds[_rng.Next(_relIds.Length)];
        return _readTx.GetProperty(id, "weight").Int64Value;
    }

    [Benchmark(Description = "CreateRel + SetProperty + Commit")]
    public RelationshipId InsertRelWithProperty()
    {
        using var tx = _db.BeginTransaction();
        var leaf = tx.CreateNode("Leaf");
        var rel = tx.CreateRelationship(_hub, leaf, "LINK");
        tx.SetProperty(rel, "weight", PropertyValue.FromInt64(1));
        tx.Commit();
        return rel;
    }

    [Benchmark(Description = "ProjectionScan weight (inline read)")]
    public long ProjectionScanWeights()
    {
        long sum = 0;
        for (int i = 0; i < _relIds.Length; i++)
            sum += _readTx.GetProperty(_relIds[i], "weight").Int64Value;
        return sum;
    }

    [Benchmark(Description = "ProjectionScan weight (columnar dense array)")]
    public long ProjectionScanWeightsColumnar()
    {
        long sum = 0;
        var key = _weightColumn.KeyId;
        for (int i = 0; i < _relIds.Length; i++)
            if (_weightColumn.TryGetScalar(_relIds[i], key, out _, out long bits))
                sum += bits;
        return sum;
    }

    // Case B: 単一エッジ point read を列指向 (dense array) で。inline の GetProperty(warm) と比較し、
    // 列指向が point read を速くしない (むしろ overhead 側) ことを確認する。
    [Benchmark(Description = "PointRead weight (columnar)")]
    public long PointReadColumnar()
    {
        var id = _relIds[_rng.Next(_relIds.Length)];
        return _weightColumn.TryGetScalar(id, _weightColumn.KeyId, out _, out long bits) ? bits : 0;
    }

    // 列の build/rebuild コスト (全 rel を 1 パス走査)。常時 RW では commit のたびにこれが要るため、
    // 「rebuild 1 回 = projection scan 何回分か」= join index 償却閾値を可視化する。
    [Benchmark(Description = "Build column (rebuild cost)")]
    public long BuildColumn()
    {
        var idx = _db.BuildRelationshipPropertyJoinIndex("weight", PropertyValueType.Int64);
        return idx.EntryCount;
    }
}
