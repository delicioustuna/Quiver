using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-5: ShortestPathOperator vs BidirectionalExpandOperator の比較。
/// Setup: 0 → 1 → 2 → ... → PathLength の線形チェーンを BulkLoader で構築。
/// クエリ: node 0 から node PathLength までの最短経路を求める。
/// </summary>
[MemoryDiagnoser]
public class ShortestPathBenchmarks
{
    [Params(10, 50, 100)]
    public int PathLength { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;
    private NodeId _srcNode;
    private NodeId _tgtNode;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_sp_" + Guid.NewGuid().ToString("N")[..8]);
        {
            using var db = GraphDatabase.Open(_dbPath);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            for (int i = 0; i <= PathLength; i++)
                loader.AppendNode(new NodeId(i), new LabelId(0));
            for (int i = 0; i < PathLength; i++)
                loader.AppendRelationship(new RelationshipId(i),
                    new NodeId(i), new NodeId(i + 1), new RelationshipTypeId(0));
            loader.Commit();
        }
        _db = GraphDatabase.Open(_dbPath);
        _srcNode = new NodeId(0);
        _tgtNode = new NodeId(PathLength);
        _readTx = _db.BeginTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }

    [Benchmark(Baseline = true, Description = "ShortestPath (BFS)")]
    public long ShortestPathBfs()
    {
        var plan = new ShortestPathOperator(
            new PairNodeSource(_srcNode, _tgtNode),
            sourceNodeColumn: 0, targetNodeColumn: 1,
            Direction.Outgoing, typeFilter: null);
        using var result = _readTx.Execute(plan);
        if (result.Statistics.RowsProduced == 0) return -1;
        return result.Rows().First().GetInt64(2);
    }

    [Benchmark(Description = "ShortestPath (Bidirectional BFS)")]
    public long ShortestPathBidir()
    {
        var plan = new BidirectionalExpandOperator(
            new PairNodeSource(_srcNode, _tgtNode),
            sourceNodeColumn: 0, targetNodeColumn: 1,
            Direction.Outgoing, typeFilter: null);
        using var result = _readTx.Execute(plan);
        if (result.Statistics.RowsProduced == 0) return -1;
        return result.Rows().First().GetInt64(2);
    }
}

/// <summary>単一 (source, target) ペアを1行だけ出力する source operator。</summary>
internal sealed class PairNodeSource : IPhysicalOperator
{
    private readonly NodeId _src;
    private readonly NodeId _tgt;
    private bool _emitted;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("source", TupleSlotType.NodeId),
        new ColumnDefinition("target", TupleSlotType.NodeId)]);

    public PairNodeSource(NodeId src, NodeId tgt) { _src = src; _tgt = tgt; }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _emitted = false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _src.Value };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _tgt.Value };
    }

    public bool MoveNext()
    {
        if (_emitted) return false;
        _emitted = true;
        return true;
    }

    public void Dispose() { }
}
