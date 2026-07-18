using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// Edge propertyのinline経路を計測する。
/// <list type="bullet">
///   <item><c>LookupEdgeProperty</c> — 単一 edge の inline property read (版チェーン walk 1 段)。</item>
///   <item><c>InsertEdgeWithProperty</c> — edge 作成 + scalar property + commit。</item>
///   <item><c>ProjectionScanWeights</c> — 全 edge の weight を読む projection スキャン。
///     これが Phase 5 (列指向) の対象ワークロード — 版チェーン walk を列セグメント逐次走査に
///     置換できれば高速化する。本ベンチの値が Phase 5 着手要否の実測根拠になる。</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class EdgePropertyBenchmarks
{
    [Params(10_000, 100_000)]
    public int RelCount { get; set; }

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private EdgeId[] _edgeIds = null!;
    private IReadTransaction _readTx = null!;
    private VertexId _hub;
    private Quiver.Storage.Records.IEdgePropertyJoinIndex _weightColumn = null!;
    private readonly Random _rng = new(42);

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("relprop");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _edgeIds = new EdgeId[RelCount];

        const int BatchSize = 5_000;
        // hub 1 個 + leaf を都度作って hub→leaf エッジに weight を inline で持たせる。
        using (var seed = _db.BeginWriteTransaction())
        {
            _hub = seed.CreateVertex("Hub");
            seed.Commit();
        }

        for (int i = 0; i < RelCount; i += BatchSize)
        {
            using var tx = _db.BeginWriteTransaction();
            int end = Math.Min(i + BatchSize, RelCount);
            for (int j = i; j < end; j++)
            {
                var leaf = tx.CreateVertex("Leaf");
                var edge = tx.CreateEdge(_hub, leaf, "LINK");
                tx.SetProperty(edge, "weight", PropertyValue.FromInt64(j));
                _edgeIds[j] = edge;
            }
            tx.Commit();
        }

        _readTx = _db.BeginWriteTransaction();

        // Phase 5 列指向 proto: dense Sequence→value 列セグメント (join index) を 1 回構築。
        // projection はこれを逐次走査でき per-edge page pin / chain walk を回避する。
        _weightColumn = _db.BuildEdgePropertyJoinIndex("weight", PropertyValueType.Int64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "GetProperty(edge) inline (warm)")]
    public long LookupEdgeProperty()
    {
        var id = _edgeIds[_rng.Next(_edgeIds.Length)];
        return _readTx.GetProperty(id, "weight").Int64Value;
    }

    [Benchmark(Description = "CreateEdge + SetProperty + Commit")]
    public EdgeId InsertEdgeWithProperty()
    {
        using var tx = _db.BeginWriteTransaction();
        var leaf = tx.CreateVertex("Leaf");
        var edge = tx.CreateEdge(_hub, leaf, "LINK");
        tx.SetProperty(edge, "weight", PropertyValue.FromInt64(1));
        tx.Commit();
        return edge;
    }

    [Benchmark(Description = "ProjectionScan weight (inline read)")]
    public long ProjectionScanWeights()
    {
        long sum = 0;
        for (int i = 0; i < _edgeIds.Length; i++)
            sum += _readTx.GetProperty(_edgeIds[i], "weight").Int64Value;
        return sum;
    }

    [Benchmark(Description = "ProjectionScan weight (columnar dense array)")]
    public long ProjectionScanWeightsColumnar()
    {
        long sum = 0;
        var key = _weightColumn.KeyId;
        for (int i = 0; i < _edgeIds.Length; i++)
            if (_weightColumn.TryGetScalar(_edgeIds[i], key, out _, out long bits))
                sum += bits;
        return sum;
    }

    // Case B: 単一エッジ point read を列指向 (dense array) で。inline の GetProperty(warm) と比較し、
    // 列指向が point read を速くしない (むしろ overhead 側) ことを確認する。
    [Benchmark(Description = "PointRead weight (columnar)")]
    public long PointReadColumnar()
    {
        var id = _edgeIds[_rng.Next(_edgeIds.Length)];
        return _weightColumn.TryGetScalar(id, _weightColumn.KeyId, out _, out long bits) ? bits : 0;
    }

    // 列の build/rebuild コスト (全 edge を 1 パス走査)。常時 RW では commit のたびにこれが要るため、
    // 「rebuild 1 回 = projection scan 何回分か」= join index 償却閾値を可視化する。
    [Benchmark(Description = "Build column (rebuild cost)")]
    public long BuildColumn()
    {
        var idx = _db.BuildEdgePropertyJoinIndex("weight", PropertyValueType.Int64);
        return idx.EntryCount;
    }
}
