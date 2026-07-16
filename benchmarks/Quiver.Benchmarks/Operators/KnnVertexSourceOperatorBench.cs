using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="KnnVertexSourceOperator"/> top-K=10 over 100 vectors dim=16.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class KnnVertexSourceOperatorBench
{
    private const int Dim = 16;
    private const string IndexName = "knn_bench";

    private string _dir = null!;
    private QuiverDatabase _db = null!;
    private IGraphTransaction _readTx = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = BenchTempDir.Create("knn");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var keyId = _db.Schema.GetOrCreatePropertyKey("embed");
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "bench", null));

        var rng = new Random(2026);
        var buf = new float[Dim];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var n = tx.CreateVertex("N");
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                _db.Vectors.SetVector(EntityKind.Vertex, n.Value, IndexName, buf);
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
    public int Knn_k10()
    {
        using var op = new KnnVertexSourceOperator(IndexName, _query, k: 10);
        return OperatorBenchDrain.Drain(op, _readTx);
    }
}
