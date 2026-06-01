using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// PW-4 隣接ブロック vs linked-list の 2-hop 比較。
/// hub(1) → mid(Degree) → leaf(Degree^2) のスター2段構造。
/// </summary>
[MemoryDiagnoser]
public class TwoHopAdjBenchmarks
{
    [Params(10, 50, 100)]
    public int Degree { get; set; }

    private GraphDatabase _db = null!;
    private string _dbPath = null!;
    private NodeId _hub;
    private IGraphTransaction _readTx = null!;
    private readonly AdjacencyEntry[] _l1Buf = new AdjacencyEntry[16_384];
    private readonly AdjacencyEntry[] _l2Buf = new AdjacencyEntry[16_384];

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("2hop_adj");
        {
            using var db = GraphDatabase.Open(_dbPath);
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);

            // Node IDs: hub=0, mid=[1..Degree], leaf=[Degree+1..]
            loader.AppendNode(new NodeId(0), new LabelId(0)); // hub
            long relId = 0;
            for (int m = 0; m < Degree; m++)
            {
                long midId = 1 + m;
                loader.AppendNode(new NodeId(midId), new LabelId(1));
                loader.AppendRelationship(new RelationshipId(relId++),
                    new NodeId(0), new NodeId(midId), new RelationshipTypeId(0));

                for (int l = 0; l < Degree; l++)
                {
                    long leafId = 1 + Degree + m * Degree + l;
                    loader.AppendNode(new NodeId(leafId), new LabelId(2));
                    loader.AppendRelationship(new RelationshipId(relId++),
                        new NodeId(midId), new NodeId(leafId), new RelationshipTypeId(0));
                }
            }
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
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "2-hop LinkedList", Baseline = true)]
    public int LinkedList()
    {
        int count = 0;
        var en1 = _readTx.EnumerateRelationships(_hub, Direction.Outgoing);
        while (en1.MoveNext())
        {
            var mid = en1.Current.Target;
            var en2 = _readTx.EnumerateRelationships(mid, Direction.Outgoing);
            while (en2.MoveNext()) count++;
        }
        return count;
    }

    [Benchmark(Description = "2-hop AdjacencyBlock")]
    public int AdjacencyBlock()
    {
        var adj = _readTx.AsInternal().AdjacencyBlocks!;
        int count = 0;
        int midCount = adj.ReadEdges(_hub, Direction.Outgoing, null, _l1Buf);
        for (int i = 0; i < midCount; i++)
            count += adj.ReadEdges(_l1Buf[i].NeighborId, Direction.Outgoing, null, _l2Buf);
        return count;
    }
}
