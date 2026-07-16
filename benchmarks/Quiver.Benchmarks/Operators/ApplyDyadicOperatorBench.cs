using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary>
///  sentinel: <see cref="ApplyDyadicOperator"/> gather/score pipeline.
/// Measures engine overhead per candidate (target: &lt;= 2 us/candidate, 0 alloc during scan).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ApplyDyadicOperatorBench
{
    private string _dir = null!;
    private QuiverDatabase _db = null!;
    private IGraphTransaction _readTx = null!;
    private VertexId[] _candidates = null!;
    private float[] _query = null!;

    [Params(50, 1000)]
    public int CandidateCount { get; set; }

    private const int Dim = 256;
    private const string IndexName = "dyadic_bench";

    [GlobalSetup]
    public void Setup()
    {
        _dir = BenchTempDir.Create("dyadic");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.Schema.GetOrCreatePropertyKey("embed");
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "bench", null, VectorIndexKind.FlatOnly));

        var rng = new Random(2026);
        var buf = new float[Dim];

        _candidates = new VertexId[CandidateCount];
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < CandidateCount; i++)
            {
                var n = tx.CreateVertex("N");
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                _db.Vectors.SetVector(EntityKind.Vertex, n.Value, IndexName, buf);
                _candidates[i] = n;
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

    [Benchmark(Description = "CosineSimilarityOp k=10")]
    public int Cosine_k10()
    {
        DyadicScoreFunc scorer = (a, b, regions) =>
        {
            var op = new CosineSimilarityOp();
            return op.Invoke(a, b, regions);
        };
        var src = new VertexArraySource(_candidates);
        using var op = new ApplyDyadicOperator(
            src, 0, IndexName, _query, null, 0, null, 10, scorer, typeof(CosineSimilarityOp));
        return OperatorBenchDrain.Drain(op, _readTx);
    }

    [Benchmark(Description = "NoOp (engine overhead) k=10")]
    public int NoOp_k10()
    {
        DyadicScoreFunc scorer = static (a, b, _) => 1.0f;
        var src = new VertexArraySource(_candidates);
        using var op = new ApplyDyadicOperator(
            src, 0, IndexName, _query, null, 0, null, 10, scorer, typeof(CosineSimilarityOp));
        return OperatorBenchDrain.Drain(op, _readTx);
    }
}
