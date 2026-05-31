using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// BA-6 / codex_advice_3 §7.2: weighted-edge read via the V2 payload lane vs
/// the property-chain join used by linked-list traversal. The hot path here
/// is "for each out-edge of the hub, sum the weight" — the kind of inner loop
/// SSSP / top-k neighbor / weighted PageRank perform.
///
/// PayloadLane:   AdjacencyBlockStoreV2 inline lane (no property fetch).
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

    private GraphDatabase _v2Db = null!;
    private GraphDatabase _v1Db = null!;
    private string _v2Path = null!;
    private string _v1Path = null!;
    private IGraphTransaction _v2Tx = null!;
    private IGraphTransaction _v1Tx = null!;
    private NodeId _hub;
    private const string WeightProp = "weight";

    [GlobalSetup]
    public void Setup()
    {
        // V2 path: bulk-load with payload lane configured, weights inlined.
        _v2Path = BenchTempDir.Create("v2");
        {
            using var db = GraphDatabase.Open(_v2Path);
            var key = db.Schema.GetOrCreatePropertyKey(WeightProp);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.WithPayloadLane(PayloadLaneSpec.ForInt64(key.Value));
            loader.AppendNode(new NodeId(0), new LabelId(0));
            for (int i = 1; i <= Degree; i++)
            {
                loader.AppendNode(new NodeId(i), new LabelId(1));
                loader.AppendRelationship(new RelationshipId(i - 1),
                    new NodeId(0), new NodeId(i), new RelationshipTypeId(0));
                loader.AppendRelationshipPayload(new RelationshipId(i - 1), key, 100 + i);
            }
            loader.Commit();
        }
        _v2Db = GraphDatabase.Open(_v2Path);
        _v2Tx = _v2Db.BeginTransaction();

        // V1 path: bulk-load without payload lane, then set the weight via
        // relationship properties so the linked-list walk has something to
        // fetch. Two-phase so the property chain exercises the same lookup
        // cost a non-V2 user would pay.
        _v1Path = BenchTempDir.Create("v1");
        {
            using var db = GraphDatabase.Open(_v1Path);
            using (var loader = db.BeginBulkLoad(buildAdjacencyIndex: false))
            {
                loader.AppendNode(new NodeId(0), new LabelId(0));
                for (int i = 1; i <= Degree; i++)
                {
                    loader.AppendNode(new NodeId(i), new LabelId(1));
                    loader.AppendRelationship(new RelationshipId(i - 1),
                        new NodeId(0), new NodeId(i), new RelationshipTypeId(0));
                }
                loader.Commit();
            }
            using var tx = db.BeginTransaction();
            for (int i = 1; i <= Degree; i++)
            {
                tx.SetProperty(new RelationshipId(i - 1), WeightProp, PropertyValue.FromInt64(100L + i));
            }
            tx.Commit();
        }
        _v1Db = GraphDatabase.Open(_v1Path);
        _v1Tx = _v1Db.BeginTransaction();

        _hub = new NodeId(0);
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

    [Benchmark(Description = "PayloadLane sum (V2 inline)")]
    public long PayloadLane()
    {
        long sum = 0;
        using var cursor = _v2Tx.AdjacencyBlocks!.OpenCursor(_hub, Direction.Outgoing, null);
        while (cursor.MoveNext())
            sum += cursor.WeightRaw;
        return sum;
    }

    [Benchmark(Description = "PropertyChain sum (linked-list + GetProperty)", Baseline = true)]
    public long PropertyChain()
    {
        long sum = 0;
        var en = _v1Tx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en.MoveNext())
        {
            var v = _v1Tx.GetProperty(en.Current.Id, WeightProp);
            sum += v.Int64Value;
        }
        return sum;
    }
}
