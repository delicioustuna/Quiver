using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// <see cref="QueryOptimizer.SelectExpandPlan"/> が選ぶ
/// <see cref="ExpandStrategy"/> ごとに 1 ホップ展開のコストを比較する regression sentinel。
///
/// 同一グラフ (5,000 vertices / EdgesPerSource=4) に対し、frontier サイズを
/// {0.10, 0.50, 0.95} で振り、それぞれの frontier に対して 3 種類のプランを
/// 強制実行する:
///   - <see cref="ExpandStrategy.AdjacencyBlock"/> (= <see cref="ExpandOperator"/>) — 既定の per-vertex 経路
///   - <see cref="ExpandStrategy.EdgeScan"/> — 全エッジ scan + bitset probe
///   - Optimizer 選択 (frontierSize hint を渡して <see cref="ExpandPlan.Build"/>)
///
/// 期待動作: 小さい frontier では AdjacencyBlock が、大きい frontier では Scan が、
/// それぞれ Optimizer 選択とほぼ一致する。Optimizer ロジック改修時に
/// 「frontier=0.10 で Scan が選ばれた」「frontier=0.95 で per-vertex が選ばれた」
/// 等の回帰を benchmark の wall-clock 差で検知する。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class OptimizerPlanRegressionBenchmarks
{
    private const int VertexCount = 5_000;
    private const int EdgesPerSource = 4;

    [Params(0.10, 0.50, 0.95)]
    public double FrontierFraction { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private IReadTransaction _readTx = null!;
    private VertexId[] _frontier = null!;
    private QueryOptimizer _optimizer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("optplan");
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));

        var rng = new Random(123);
        var ids = new VertexId[VertexCount];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < VertexCount; i++) ids[i] = tx.CreateVertex("N");
            for (int i = 0; i < VertexCount; i++)
                for (int e = 0; e < EdgesPerSource; e++)
                    tx.CreateEdge(ids[i], ids[rng.Next(VertexCount)], "E");
            tx.Commit();
        }

        int k = (int)(VertexCount * FrontierFraction);
        _frontier = new VertexId[k];
        Array.Copy(ids, _frontier, k);

        var stats = _db.CollectStats();
        _optimizer = _db.CreateOptimizer(stats);
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

    [Benchmark(Baseline = true, Description = "Forced AdjacencyBlock (per-vertex ExpandOperator)")]
    public int ForcedAdjacencyBlock()
    {
        var src = new FrontierSourceOperator(_frontier);
        using var op = new ExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "Forced EdgeScan (edge-scan operator)")]
    public int ForcedEdgeScan()
    {
        var src = new FrontierSourceOperator(_frontier);
        using var op = new EdgeScanExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "Optimizer-selected (with frontierSize hint)")]
    public int OptimizerSelected()
    {
        var plan = _optimizer.SelectExpandPlan(
            sourceLabel: null, typeFilter: null, direction: Direction.Outgoing,
            frontierSize: _frontier.Length);
        var src = new FrontierSourceOperator(_frontier);
        using var op = plan.Build(src, sourceVertexColumn: 0, Direction.Outgoing, typeFilter: null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    /// <summary>Frontier Vertex列を物理オペレータとして放出する小さい source.</summary>
    private sealed class FrontierSourceOperator : IPhysicalOperator
    {
        private readonly VertexId[] _vertices;
        private int _index = -1;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        public FrontierSourceOperator(VertexId[] vertices) => _vertices = vertices;
        public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(ITransaction tx) { _index = -1; }
        public bool MoveNext()
        {
            if (++_index >= _vertices.Length) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _vertices[_index].Value };
            return true;
        }
        public void Dispose() { }
    }
}
