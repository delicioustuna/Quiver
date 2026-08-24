using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// Sequential BfsOperator vs ParallelBfsOperator for multi-source 2-hop / 3-hop traversal.
///
/// Graph shape: <paramref name="Sources"/> independent hub vertices, each connected to
/// <paramref name="Degree"/> L1 vertices. L1 vertices each connect to <paramref name="Degree"/> L2 vertices.
/// ParallelBfsOperator fans out one BFS task per source hub and runs them in parallel.
/// </summary>
[MemoryDiagnoser]
public class ParallelBenchmarks
{
    [Params(8, 16)]
    public int Sources { get; set; }

    [Params(10)]
    public int Degree { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private VertexId[] _sourceVertices = [];
    private IReadTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("par");
        {
            using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            long vertexId = 0;
            long edgeId = 0;
            var hubIds = new long[Sources];

            for (int s = 0; s < Sources; s++)
            {
                hubIds[s] = vertexId;
                loader.AppendVertex(new VertexId(vertexId++), new LabelId(0));

                long l1Start = vertexId;
                for (int i = 0; i < Degree; i++)
                    loader.AppendVertex(new VertexId(vertexId++), new LabelId(1));
                for (int i = 0; i < Degree; i++)
                    loader.AppendEdge(new EdgeId(edgeId++),
                        new VertexId(hubIds[s]), new VertexId(l1Start + i), new EdgeTypeId(0));

                long l2Start = vertexId;
                for (int i = 0; i < Degree; i++)
                    for (int j = 0; j < Degree; j++)
                        loader.AppendVertex(new VertexId(vertexId++), new LabelId(2));
                for (int i = 0; i < Degree; i++)
                    for (int j = 0; j < Degree; j++)
                        loader.AppendEdge(new EdgeId(edgeId++),
                            new VertexId(l1Start + i), new VertexId(l2Start + i * Degree + j), new EdgeTypeId(0));
            }

            loader.Commit();
        }

        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));
        _sourceVertices = new VertexId[Sources];
        // hub vertices are at stride = 1 + Degree + Degree*Degree per source
        long stride = 1 + Degree + (long)Degree * Degree;
        for (int s = 0; s < Sources; s++)
            _sourceVertices[s] = new VertexId(s * stride);

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

    // ── 2-hop ──────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "2-hop sequential BfsOperator")]
    public long TwoHopSequential()
    {
        long total = 0;
        foreach (var hub in _sourceVertices)
        {
            var plan = new BfsOperator(
                new SingleVertexSource(hub), sourceVertexColumn: 0,
                Direction.Outgoing, typeFilter: null, maxDepth: 2);
            using var result = _readTx.Execute(plan);
            total += result.Statistics.RowsProduced;
        }
        return total;
    }

    [Benchmark(Description = "2-hop parallel BfsOperator(maxParallelism:-1)")]
    public long TwoHopParallel()
    {
        var plan = new BfsOperator(
            new MultiVertexSource(_sourceVertices), sourceVertexColumn: 0,
            Direction.Outgoing, typeFilter: null, maxDepth: 2, maxParallelism: -1);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    // ── 3-hop ──────────────────────────────────────────────────────────────

    [Benchmark(Description = "3-hop sequential BfsOperator")]
    public long ThreeHopSequential()
    {
        long total = 0;
        foreach (var hub in _sourceVertices)
        {
            var plan = new BfsOperator(
                new SingleVertexSource(hub), sourceVertexColumn: 0,
                Direction.Outgoing, typeFilter: null, maxDepth: 3);
            using var result = _readTx.Execute(plan);
            total += result.Statistics.RowsProduced;
        }
        return total;
    }

    [Benchmark(Description = "3-hop parallel BfsOperator(maxParallelism:-1)")]
    public long ThreeHopParallel()
    {
        var plan = new BfsOperator(
            new MultiVertexSource(_sourceVertices), sourceVertexColumn: 0,
            Direction.Outgoing, typeFilter: null, maxDepth: 3, maxParallelism: -1);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }
}

/// <summary>複数 VertexId を順番に1行ずつ出力する source operator。</summary>
internal sealed class MultiVertexSource : IPhysicalOperator
{
    private readonly VertexId[] _ids;
    private int _idx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);

    public MultiVertexSource(VertexId[] ids) => _ids = ids;

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) => _idx = 0;

    public bool MoveNext()
    {
        if (_idx >= _ids.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _ids[_idx++].Value };
        return true;
    }

    public void Dispose() { }
}
