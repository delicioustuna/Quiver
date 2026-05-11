using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-5: 手動トラバーサル vs BfsOperator / VariableLengthExpandOperator の比較。
/// Setup: BulkLoader(buildAdjacencyIndex:true) で hub → L1 → L2 → L3 の3段ツリー。
/// </summary>
[MemoryDiagnoser]
public class MultiHopOperatorBenchmarks
{
    [Params(5, 10)]
    public int Degree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId _hub;
    private IGraphTransaction _readTx = null!;

    // LabelId mapping (BulkLoader で直接 int 指定)
    private static readonly LabelId HubLabel = new(0);

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "quiver_bench_mhop_" + Guid.NewGuid().ToString("N")[..8]);
        {
            using var db = GraphDatabase.Open(_dbPath);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            long nodeId = 0;
            long relId = 0;
            long hubRawId = nodeId;
            loader.AppendNode(new NodeId(nodeId++), new LabelId(0)); // hub

            // hub → L1
            long l1Start = nodeId;
            for (int i = 0; i < Degree; i++)
                loader.AppendNode(new NodeId(nodeId++), new LabelId(1));
            for (int i = 0; i < Degree; i++)
                loader.AppendRelationship(new RelationshipId(relId++),
                    new NodeId(hubRawId), new NodeId(l1Start + i), new RelationshipTypeId(0));

            // L1 → L2
            long l2Start = nodeId;
            for (int i = 0; i < Degree; i++)
                for (int j = 0; j < Degree; j++)
                    loader.AppendNode(new NodeId(nodeId++), new LabelId(2));
            for (int i = 0; i < Degree; i++)
                for (int j = 0; j < Degree; j++)
                    loader.AppendRelationship(new RelationshipId(relId++),
                        new NodeId(l1Start + i), new NodeId(l2Start + i * Degree + j), new RelationshipTypeId(0));

            // L2 → L3
            long l3Start = nodeId;
            int l2Count = Degree * Degree;
            for (int i = 0; i < l2Count; i++)
                for (int j = 0; j < Degree; j++)
                    loader.AppendNode(new NodeId(nodeId++), new LabelId(3));
            for (int i = 0; i < l2Count; i++)
                for (int j = 0; j < Degree; j++)
                    loader.AppendRelationship(new RelationshipId(relId++),
                        new NodeId(l2Start + i), new NodeId(l3Start + i * Degree + j), new RelationshipTypeId(0));

            loader.Commit();
        }
        _db = GraphDatabase.Open(_dbPath);
        _hub = new NodeId(0);
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

    // ── 2-hop ──────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "2-hop manual")]
    public int TwoHopManual()
    {
        int count = 0;
        var en1 = _readTx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var en2 = _readTx.EnumerateRelationships(en1.Current.Target, Direction.Outgoing);
            while (en2.MoveNext()) count++;
        }
        return count;
    }

    [Benchmark(Description = "2-hop BfsOperator")]
    public long TwoHopBfsOperator()
    {
        var plan = new BfsOperator(
            new SingleNodeSource(_hub), sourceNodeColumn: 0,
            Direction.Outgoing, typeFilter: null, maxDepth: 2);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "2-hop VariableLengthExpand")]
    public long TwoHopVLExpand()
    {
        var plan = new VariableLengthExpandOperator(
            new SingleNodeSource(_hub), sourceNodeColumn: 0,
            Direction.Outgoing, typeFilter: null, minHops: 1, maxHops: 2);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    // ── 3-hop ──────────────────────────────────────────────────────────────

    [Benchmark(Description = "3-hop manual")]
    public int ThreeHopManual()
    {
        int count = 0;
        var en1 = _readTx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var en2 = _readTx.EnumerateRelationships(en1.Current.Target, Direction.Outgoing);
            while (en2.MoveNext())
            {
                var en3 = _readTx.EnumerateRelationships(en2.Current.Target, Direction.Outgoing);
                while (en3.MoveNext()) count++;
            }
        }
        return count;
    }

    [Benchmark(Description = "3-hop BfsOperator")]
    public long ThreeHopBfsOperator()
    {
        var plan = new BfsOperator(
            new SingleNodeSource(_hub), sourceNodeColumn: 0,
            Direction.Outgoing, typeFilter: null, maxDepth: 3);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "3-hop VariableLengthExpand")]
    public long ThreeHopVLExpand()
    {
        var plan = new VariableLengthExpandOperator(
            new SingleNodeSource(_hub), sourceNodeColumn: 0,
            Direction.Outgoing, typeFilter: null, minHops: 1, maxHops: 3);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }
}

/// <summary>単一 NodeId を1行だけ出力する source operator。</summary>
internal sealed class SingleNodeSource : IPhysicalOperator
{
    private readonly NodeId _id;
    private bool _emitted;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);

    public SingleNodeSource(NodeId id) => _id = id;

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _emitted = false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _id.Value };
    }

    public bool MoveNext()
    {
        if (_emitted) return false;
        _emitted = true;
        return true;
    }

    public void Dispose() { }
}
