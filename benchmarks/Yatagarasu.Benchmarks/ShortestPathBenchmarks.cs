using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// ShortestPathOperator vs BidirectionalExpandOperator の比較。
/// Setup: 0 → 1 → 2 → ... → PathLength の線形チェーンを BulkLoader で構築。
/// クエリ: vertex 0 から vertex PathLength までの最短経路を求める。
/// </summary>
[MemoryDiagnoser]
public class ShortestPathBenchmarks
{
    [Params(10, 50, 100)]
    public int PathLength { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private IReadTransaction _readTx = null!;
    private VertexId _srcVertex;
    private VertexId _tgtVertex;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("sp");
        {
            using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            for (int i = 0; i <= PathLength; i++)
                loader.AppendVertex(new VertexId(i), new LabelId(0));
            for (int i = 0; i < PathLength; i++)
                loader.AppendEdge(new EdgeId(i),
                    new VertexId(i), new VertexId(i + 1), new EdgeTypeId(0));
            loader.Commit();
        }
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));
        _srcVertex = new VertexId(0);
        _tgtVertex = new VertexId(PathLength);
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

    [Benchmark(Baseline = true, Description = "ShortestPath (BFS)")]
    public long ShortestPathBfs()
    {
        var plan = new ShortestPathOperator(
            new PairVertexSource(_srcVertex, _tgtVertex),
            sourceVertexColumn: 0, targetVertexColumn: 1,
            Direction.Outgoing, typeFilter: null);
        using var result = _readTx.Execute(plan);
        if (result.Statistics.RowsProduced == 0) return -1;
        return result.Rows().First().GetInt64(2);
    }

    [Benchmark(Description = "ShortestPath (Bidirectional BFS)")]
    public long ShortestPathBidir()
    {
        var plan = new BidirectionalExpandOperator(
            new PairVertexSource(_srcVertex, _tgtVertex),
            sourceVertexColumn: 0, targetVertexColumn: 1,
            Direction.Outgoing, typeFilter: null);
        using var result = _readTx.Execute(plan);
        if (result.Statistics.RowsProduced == 0) return -1;
        return result.Rows().First().GetInt64(2);
    }
}

/// <summary>単一 (source, target) ペアを1行だけ出力する source operator。</summary>
internal sealed class PairVertexSource : IPhysicalOperator
{
    private readonly VertexId _src;
    private readonly VertexId _tgt;
    private bool _emitted;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("source", TupleSlotType.VertexId),
        new ColumnDefinition("target", TupleSlotType.VertexId)]);

    public PairVertexSource(VertexId src, VertexId tgt) { _src = src; _tgt = tgt; }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _emitted = false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _src.Value };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _tgt.Value };
    }

    public bool MoveNext()
    {
        if (_emitted) return false;
        _emitted = true;
        return true;
    }

    public void Dispose() { }
}
