using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-18: <see cref="QueryOptimizer.SelectExpandPlan"/> が選ぶ
/// <see cref="ExpandStrategy"/> ごとに 1 ホップ展開のコストを比較する regression sentinel。
///
/// 同一グラフ (5,000 nodes / EdgesPerSource=4) に対し、frontier サイズを
/// {0.10, 0.50, 0.95} で振り、それぞれの frontier に対して 3 種類のプランを
/// 強制実行する:
///   - <see cref="ExpandStrategy.AdjacencyBlock"/> (= <see cref="ExpandOperator"/>) — 既定の per-node 経路
///   - <see cref="ExpandStrategy.RelationshipScan"/> — 全エッジ scan + bitset probe
///   - Optimizer 選択 (frontierSize hint を渡して <see cref="ExpandPlan.Build"/>)
///
/// 期待動作: 小さい frontier では AdjacencyBlock が、大きい frontier では Scan が、
/// それぞれ Optimizer 選択とほぼ一致する。Optimizer ロジック改修時に
/// 「frontier=0.10 で Scan が選ばれた」「frontier=0.95 で per-node が選ばれた」
/// 等の回帰を benchmark の wall-clock 差で検知する。
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class OptimizerPlanRegressionBenchmarks
{
    private const int NodeCount = 5_000;
    private const int EdgesPerSource = 4;

    [Params(0.10, 0.50, 0.95)]
    public double FrontierFraction { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;
    private NodeId[] _frontier = null!;
    private QueryOptimizer _optimizer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("optplan");
        _db = GraphDatabase.Open(_dbPath);

        var rng = new Random(123);
        var ids = new NodeId[NodeCount];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < NodeCount; i++) ids[i] = tx.CreateNode("N");
            for (int i = 0; i < NodeCount; i++)
                for (int e = 0; e < EdgesPerSource; e++)
                    tx.CreateRelationship(ids[i], ids[rng.Next(NodeCount)], "E");
            tx.Commit();
        }

        int k = (int)(NodeCount * FrontierFraction);
        _frontier = new NodeId[k];
        Array.Copy(ids, _frontier, k);

        var stats = _db.CollectStats();
        _optimizer = _db.CreateOptimizer(stats);
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

    [Benchmark(Baseline = true, Description = "Forced AdjacencyBlock (per-node ExpandOperator)")]
    public int ForcedAdjacencyBlock()
    {
        var src = new FrontierSourceOperator(_frontier);
        using var op = new ExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "Forced RelationshipScan (PW-17 operator)")]
    public int ForcedRelationshipScan()
    {
        var src = new FrontierSourceOperator(_frontier);
        using var op = new RelationshipScanExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
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
        using var op = plan.Build(src, sourceNodeColumn: 0, Direction.Outgoing, typeFilter: null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    /// <summary>Frontier ノード列を物理オペレータとして放出する小さい source.</summary>
    private sealed class FrontierSourceOperator : IPhysicalOperator
    {
        private readonly NodeId[] _nodes;
        private int _index = -1;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        public FrontierSourceOperator(NodeId[] nodes) => _nodes = nodes;
        public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);
        public void Open(ITransaction tx) { _index = -1; }
        public bool MoveNext()
        {
            if (++_index >= _nodes.Length) return false;
            _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _nodes[_index].Value };
            return true;
        }
        public void Dispose() { }
    }
}
