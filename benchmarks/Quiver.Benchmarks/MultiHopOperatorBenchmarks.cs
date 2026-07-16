using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// 手動トラバーサル vs BfsOperator / VariableLengthExpandOperator の比較。
/// Setup: BulkLoader(buildAdjacencyIndex:true) で hub → L1 → L2 → L3 の3段ツリー。
/// </summary>
[MemoryDiagnoser]
public class MultiHopOperatorBenchmarks
{
    [Params(5, 10)]
    public int Degree { get; set; }

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private VertexId _hub;
    private IGraphTransaction _readTx = null!;

    // LabelId mapping (BulkLoader で直接 int 指定)
    private static readonly LabelId HubLabel = new(0);

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("mhop");
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            long vertexId = 0;
            long edgeId = 0;
            long hubRawId = vertexId;
            loader.AppendVertex(new VertexId(vertexId++), new LabelId(0)); // hub

            // hub → L1
            long l1Start = vertexId;
            for (int i = 0; i < Degree; i++)
                loader.AppendVertex(new VertexId(vertexId++), new LabelId(1));
            for (int i = 0; i < Degree; i++)
                loader.AppendEdge(new EdgeId(edgeId++),
                    new VertexId(hubRawId), new VertexId(l1Start + i), new EdgeTypeId(0));

            // L1 → L2
            long l2Start = vertexId;
            for (int i = 0; i < Degree; i++)
                for (int j = 0; j < Degree; j++)
                    loader.AppendVertex(new VertexId(vertexId++), new LabelId(2));
            for (int i = 0; i < Degree; i++)
                for (int j = 0; j < Degree; j++)
                    loader.AppendEdge(new EdgeId(edgeId++),
                        new VertexId(l1Start + i), new VertexId(l2Start + i * Degree + j), new EdgeTypeId(0));

            // L2 → L3
            long l3Start = vertexId;
            int l2Count = Degree * Degree;
            for (int i = 0; i < l2Count; i++)
                for (int j = 0; j < Degree; j++)
                    loader.AppendVertex(new VertexId(vertexId++), new LabelId(3));
            for (int i = 0; i < l2Count; i++)
                for (int j = 0; j < Degree; j++)
                    loader.AppendEdge(new EdgeId(edgeId++),
                        new VertexId(l2Start + i), new VertexId(l3Start + i * Degree + j), new EdgeTypeId(0));

            loader.Commit();
        }
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _hub = new VertexId(0);
        _readTx = _db.BeginTransaction();
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

    [Benchmark(Baseline = true, Description = "2-hop manual")]
    public int TwoHopManual()
    {
        int count = 0;
        var en1 = _readTx.EnumerateEdges(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var en2 = _readTx.EnumerateEdges(en1.Current.Target, Direction.Outgoing);
            while (en2.MoveNext()) count++;
        }
        return count;
    }

    [Benchmark(Description = "2-hop BfsOperator")]
    public long TwoHopBfsOperator()
    {
        var plan = new BfsOperator(
            new SingleVertexSource(_hub), sourceVertexColumn: 0,
            Direction.Outgoing, typeFilter: null, maxDepth: 2);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "2-hop VariableLengthExpand")]
    public long TwoHopVLExpand()
    {
        var plan = new VariableLengthExpandOperator(
            new SingleVertexSource(_hub), sourceVertexColumn: 0,
            Direction.Outgoing, typeFilter: null, minHops: 1, maxHops: 2);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    // ── 3-hop ──────────────────────────────────────────────────────────────

    [Benchmark(Description = "3-hop manual")]
    public int ThreeHopManual()
    {
        int count = 0;
        var en1 = _readTx.EnumerateEdges(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var en2 = _readTx.EnumerateEdges(en1.Current.Target, Direction.Outgoing);
            while (en2.MoveNext())
            {
                var en3 = _readTx.EnumerateEdges(en2.Current.Target, Direction.Outgoing);
                while (en3.MoveNext()) count++;
            }
        }
        return count;
    }

    [Benchmark(Description = "3-hop BfsOperator")]
    public long ThreeHopBfsOperator()
    {
        var plan = new BfsOperator(
            new SingleVertexSource(_hub), sourceVertexColumn: 0,
            Direction.Outgoing, typeFilter: null, maxDepth: 3);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "3-hop VariableLengthExpand")]
    public long ThreeHopVLExpand()
    {
        var plan = new VariableLengthExpandOperator(
            new SingleVertexSource(_hub), sourceVertexColumn: 0,
            Direction.Outgoing, typeFilter: null, minHops: 1, maxHops: 3);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }
}

/// <summary>単一 VertexId を1行だけ出力する source operator。</summary>
internal sealed class SingleVertexSource : IPhysicalOperator
{
    private readonly VertexId _id;
    private bool _emitted;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);

    public SingleVertexSource(VertexId id) => _id = id;

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _emitted = false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _id.Value };
    }

    public bool MoveNext()
    {
        if (_emitted) return false;
        _emitted = true;
        return true;
    }

    public void Dispose() { }
}
