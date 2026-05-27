using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary>TS-6 sentinel: <see cref="FilteredKnnNodeSourceOperator"/> with 50-node candidate set.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FilteredKnnNodeSourceOperatorBench
{
    private const int Dim = 16;
    private const string IndexName = "fknn_bench";

    private string _dir = null!;
    private GraphDatabase _db = null!;
    private IGraphTransaction _readTx = null!;
    private NodeId[] _candidates = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = BenchTempDir.Create("fknn");
        _db = GraphDatabase.Open(_dir);
        var keyId = _db.Schema.GetOrCreatePropertyKey("embed");
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, keyId, Dim,
            DistanceMetric.Cosine, "bench", null));

        var rng = new Random(2026);
        _candidates = new NodeId[50];
        var buf = new float[Dim];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var n = tx.CreateNode("N");
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                _db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, buf);
                if (i < 50) _candidates[i] = n;
            }
            tx.Commit();
        }

        _query = new float[Dim];
        for (int d = 0; d < Dim; d++) _query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
        _readTx = _db.BeginReadOnlyTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        BenchTempDir.Delete(_dir);
    }

    [Benchmark]
    public int FilteredKnn_k5()
    {
        var src = new NodeArraySource(_candidates);
        using var op = new FilteredKnnNodeSourceOperator(src, 0, IndexName, _query, k: 5);
        return OperatorBenchDrain.Drain(op, _readTx);
    }
}
