using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// Weighted-edge read via the adjacency payload lane vs
/// the property-chain join used by linked-list traversal. The hot path here
/// is "for each out-edge of the hub, sum the weight" — the kind of inner loop
/// SSSP / top-k neighbor / weighted PageRank perform.
///
/// PayloadLane:   AdjacencySegmentStore inline lane (no property fetch).
/// PropertyChain: linked-list walk + GetProperty per edge (existing path).
///
/// Expectation: PayloadLane should be substantially faster at any degree;
/// the gap widens as degree grows because the property chain pays a separate
/// page fetch per edge.
/// </summary>
[MemoryDiagnoser]
public class WeightedAdjBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int Degree { get; set; }

    private QuiverDatabase _v2Db = null!;
    private QuiverDatabase _v1Db = null!;
    private string _v2Path = null!;
    private string _v1Path = null!;
    private IReadTransaction _v2Tx = null!;
    private IReadTransaction _v1Tx = null!;
    private VertexId _hub;
    private const string WeightProp = "weight";

    [GlobalSetup]
    public void Setup()
    {
        // Segment path: bulk-load with payload lane configured, weights inlined.
        _v2Path = BenchTempDir.Create("v2");
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_v2Path, "graph.quiver"));
            var key = db.EditSchema(schema => schema.GetOrCreatePropertyKey(WeightProp));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.WithPayloadLane(PayloadLaneSpec.ForInt64(key.Value));
            loader.AppendVertex(new VertexId(0), new LabelId(0));
            for (int i = 1; i <= Degree; i++)
            {
                loader.AppendVertex(new VertexId(i), new LabelId(1));
                loader.AppendEdge(new EdgeId(i - 1),
                    new VertexId(0), new VertexId(i), new EdgeTypeId(0));
                loader.AppendEdgePayload(new EdgeId(i - 1), key, 100 + i);
            }
            loader.Commit();
        }
        _v2Db = QuiverDatabase.Open(System.IO.Path.Combine(_v2Path, "graph.quiver"));
        _v2Tx = _v2Db.BeginWriteTransaction();

        // Property path: bulk-load without payload lane, then set the weight via
        // edge properties so the linked-list walk has something to
        // fetch. Two-phase so the property chain exercises the same lookup
        // cost when the payload lane is disabled.
        _v1Path = BenchTempDir.Create("v1");
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_v1Path, "graph.quiver"));
            using (var loader = db.BeginBulkLoad(buildAdjacencyIndex: false))
            {
                loader.AppendVertex(new VertexId(0), new LabelId(0));
                for (int i = 1; i <= Degree; i++)
                {
                    loader.AppendVertex(new VertexId(i), new LabelId(1));
                    loader.AppendEdge(new EdgeId(i - 1),
                        new VertexId(0), new VertexId(i), new EdgeTypeId(0));
                }
                loader.Commit();
            }
            using var tx = db.BeginWriteTransaction();
            for (int i = 1; i <= Degree; i++)
            {
                tx.SetProperty(EdgeId.Create(i - 1, 1), WeightProp, PropertyValue.FromInt64(100L + i));
            }
            tx.Commit();
        }
        _v1Db = QuiverDatabase.Open(System.IO.Path.Combine(_v1Path, "graph.quiver"));
        _v1Tx = _v1Db.BeginWriteTransaction();

        _hub = new VertexId(0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _v2Tx?.Dispose();
        _v1Tx?.Dispose();
        _v2Db?.Dispose();
        _v1Db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_v2Path);
        BenchTempDir.Delete(_v1Path);
    }

    [Benchmark(Description = "PayloadLane sum (segment inline)")]
    public long PayloadLane()
    {
        long sum = 0;
        using var cursor = _v2Tx.AsInternal().AdjacencySegments!.OpenCursor(_hub, Direction.Outgoing, null);
        while (cursor.MoveNext())
            sum += cursor.WeightRaw;
        return sum;
    }

    [Benchmark(Description = "PropertyChain sum (linked-list + GetProperty)", Baseline = true)]
    public long PropertyChain()
    {
        long sum = 0;
        var en = _v1Tx.EnumerateEdges(_hub, Direction.Outgoing);
        while (en.MoveNext())
        {
            var v = _v1Tx.GetProperty(en.Current.Id, WeightProp);
            sum += v.Int64Value;
        }
        return sum;
    }
}
