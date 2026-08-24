using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// Per-vertex ExpandOperator vs sequential EdgeScanExpandOperator,
/// varying the frontier fraction over a fixed-size graph. The scan path should
/// win once the frontier covers a meaningful slice of the edges because
/// chasing N independent linked lists destroys page locality whereas the
/// sequential scan visits each edge page exactly once.
/// </summary>
[MemoryDiagnoser]
public class FrontierExpandBenchmarks
{
    private const int VertexCount = 5_000;
    private const int EdgesPerSource = 4;

    [Params(0.05, 0.25, 0.50, 1.00)]
    public double FrontierFraction { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private IReadTransaction _readTx = null!;
    private VertexId[] _frontier = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("pw17");
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));

        var rng = new Random(42);
        var ids = new VertexId[VertexCount];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < VertexCount; i++) ids[i] = tx.CreateVertex("N");
            for (int i = 0; i < VertexCount; i++)
                for (int e = 0; e < EdgesPerSource; e++)
                {
                    var t = ids[rng.Next(VertexCount)];
                    tx.CreateEdge(ids[i], t, "E");
                }
            tx.Commit();
        }

        int k = (int)(VertexCount * FrontierFraction);
        _frontier = new VertexId[k];
        Array.Copy(ids, _frontier, k);
        _readTx = _db.BeginWriteTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "Per-vertex ExpandOperator", Baseline = true)]
    public int PerVertex()
    {
        var src = new FixedFrontierOperator(_frontier);
        using var op = new ExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "EdgeScanExpand ()")]
    public int Scan()
    {
        var src = new FixedFrontierOperator(_frontier);
        using var op = new EdgeScanExpandOperator(src, 0, Direction.Outgoing, null, ExpandOutputMode.NeighborOnly);
        using var result = _readTx.Execute(op);
        return (int)result.Statistics.RowsProduced;
    }

    private sealed class FixedFrontierOperator : IPhysicalOperator
    {
        private readonly VertexId[] _vertices;
        private int _index = -1;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];
        public FixedFrontierOperator(VertexId[] vertices) => _vertices = vertices;
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
