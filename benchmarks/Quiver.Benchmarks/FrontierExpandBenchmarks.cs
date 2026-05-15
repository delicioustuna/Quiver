using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-17: Per-node ExpandOperator vs sequential RelationshipScanExpandOperator,
/// varying the frontier fraction over a fixed-size graph. The scan path should
/// win once the frontier covers a meaningful slice of the relationships because
/// chasing N independent linked lists destroys page locality whereas the
/// sequential scan visits each relationship page exactly once.
/// </summary>
[MemoryDiagnoser]
public class FrontierExpandBenchmarks
{
    private const int NodeCount = 5_000;
    private const int EdgesPerSource = 4;

    [Params(0.05, 0.25, 0.50, 1.00)]
    public double FrontierFraction { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;
    private NodeId[] _frontier = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_pw17_" + Guid.NewGuid().ToString("N")[..8]);
        _db = GraphDatabase.Open(_dbPath);

        var rng = new Random(42);
        var ids = new NodeId[NodeCount];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < NodeCount; i++) ids[i] = tx.CreateNode("N");
            for (int i = 0; i < NodeCount; i++)
                for (int e = 0; e < EdgesPerSource; e++)
                {
                    var t = ids[rng.Next(NodeCount)];
                    tx.CreateRelationship(ids[i], t, "E");
                }
            tx.Commit();
        }

        int k = (int)(NodeCount * FrontierFraction);
        _frontier = new NodeId[k];
        Array.Copy(ids, _frontier, k);
        _readTx = _db.BeginTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true);
    }

    [Benchmark(Description = "Per-node ExpandOperator", Baseline = true)]
    public int PerNode()
    {
        var src = new FixedFrontierOperator(_frontier);
        using var op = new ExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "RelationshipScanExpand (PW-17)")]
    public int Scan()
    {
        var src = new FixedFrontierOperator(_frontier);
        using var op = new RelationshipScanExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    private sealed class FixedFrontierOperator : IPhysicalOperator
    {
        private readonly NodeId[] _nodes;
        private int _index = -1;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        public FixedFrontierOperator(NodeId[] nodes) => _nodes = nodes;
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
