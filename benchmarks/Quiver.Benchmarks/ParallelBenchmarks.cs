using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-7: Sequential BfsOperator vs ParallelBfsOperator for multi-source 2-hop / 3-hop traversal.
///
/// Graph shape: <paramref name="Sources"/> independent hub nodes, each connected to
/// <paramref name="Degree"/> L1 nodes. L1 nodes each connect to <paramref name="Degree"/> L2 nodes.
/// ParallelBfsOperator fans out one BFS task per source hub and runs them in parallel.
/// </summary>
[MemoryDiagnoser]
public class ParallelBenchmarks
{
    [Params(8, 16)]
    public int Sources { get; set; }

    [Params(10)]
    public int Degree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId[] _sourceNodes = [];
    private IGraphTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("par");
        {
            using var db = GraphDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            long nodeId = 0;
            long relId = 0;
            var hubIds = new long[Sources];

            for (int s = 0; s < Sources; s++)
            {
                hubIds[s] = nodeId;
                loader.AppendNode(new NodeId(nodeId++), new LabelId(0));

                long l1Start = nodeId;
                for (int i = 0; i < Degree; i++)
                    loader.AppendNode(new NodeId(nodeId++), new LabelId(1));
                for (int i = 0; i < Degree; i++)
                    loader.AppendRelationship(new RelationshipId(relId++),
                        new NodeId(hubIds[s]), new NodeId(l1Start + i), new RelationshipTypeId(0));

                long l2Start = nodeId;
                for (int i = 0; i < Degree; i++)
                    for (int j = 0; j < Degree; j++)
                        loader.AppendNode(new NodeId(nodeId++), new LabelId(2));
                for (int i = 0; i < Degree; i++)
                    for (int j = 0; j < Degree; j++)
                        loader.AppendRelationship(new RelationshipId(relId++),
                            new NodeId(l1Start + i), new NodeId(l2Start + i * Degree + j), new RelationshipTypeId(0));
            }

            loader.Commit();
        }

        _db = GraphDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _sourceNodes = new NodeId[Sources];
        // hub nodes are at stride = 1 + Degree + Degree*Degree per source
        long stride = 1 + Degree + (long)Degree * Degree;
        for (int s = 0; s < Sources; s++)
            _sourceNodes[s] = new NodeId(s * stride);

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

    // ── 2-hop ──────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "2-hop sequential BfsOperator")]
    public long TwoHopSequential()
    {
        long total = 0;
        foreach (var hub in _sourceNodes)
        {
            var plan = new BfsOperator(
                new SingleNodeSource(hub), sourceNodeColumn: 0,
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
            new MultiNodeSource(_sourceNodes), sourceNodeColumn: 0,
            Direction.Outgoing, typeFilter: null, maxDepth: 2, maxParallelism: -1);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    // ── 3-hop ──────────────────────────────────────────────────────────────

    [Benchmark(Description = "3-hop sequential BfsOperator")]
    public long ThreeHopSequential()
    {
        long total = 0;
        foreach (var hub in _sourceNodes)
        {
            var plan = new BfsOperator(
                new SingleNodeSource(hub), sourceNodeColumn: 0,
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
            new MultiNodeSource(_sourceNodes), sourceNodeColumn: 0,
            Direction.Outgoing, typeFilter: null, maxDepth: 3, maxParallelism: -1);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }
}

/// <summary>複数 NodeId を順番に1行ずつ出力する source operator。</summary>
internal sealed class MultiNodeSource : IPhysicalOperator
{
    private readonly NodeId[] _ids;
    private int _idx;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);

    public MultiNodeSource(NodeId[] ids) => _ids = ids;

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) => _idx = 0;

    public bool MoveNext()
    {
        if (_idx >= _ids.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _ids[_idx++].Value };
        return true;
    }

    public void Dispose() { }
}
