using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary> sentinel: <see cref="FilteredKnnVertexSourceOperator"/> with 50-vertex candidate set.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FilteredKnnVertexSourceOperatorBench
{
    private const int Dim = 16;
    private const string IndexName = "fknn_bench";

    private string _dir = null!;
    private QuiverDatabase _db = null!;
    private IReadTransaction _readTx = null!;
    private VertexId[] _candidates = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = BenchTempDir.Create("fknn");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var keyId = _db.EditSchema(schema => schema.GetOrCreatePropertyKey("embed"));
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "bench", null));

        var rng = new Random(2026);
        _candidates = new VertexId[50];
        var buf = new float[Dim];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var n = tx.CreateVertex("N");
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                _db.Vectors.SetVector(EntityKind.Vertex, n.Value, IndexName, buf);
                if (i < 50) _candidates[i] = n;
            }
            tx.Commit();
        }

        _query = new float[Dim];
        for (int d = 0; d < Dim; d++) _query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
        _readTx = _db.BeginReadTransaction();
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
        var src = new VertexArraySource(_candidates);
        using var op = new FilteredKnnVertexSourceOperator(src, 0, IndexName, _query, k: 5);
        return OperatorBenchDrain.Drain(op, _readTx);
    }
}
