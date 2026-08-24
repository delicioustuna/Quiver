using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Query.Logical;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary>
///  sentinel: <see cref="ApplyDyadicOperator"/> gather/score pipeline.
/// Measures engine overhead per candidate (target: &lt;= 2 us/candidate, 0 alloc during scan).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ApplyDyadicOperatorBench
{
    private string _dir = null!;
    private YatagarasuDatabase _db = null!;
    private IReadTransaction _readTx = null!;
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
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"));

        _db.EditSchema(schema =>
        {
            schema.GetOrCreatePropertyKey("embed");
            schema.CreateIndex(new VectorIndexDefinition(
                IndexName,
                new PropertyTarget(PropertyOwnerKind.Vertex, "embed", "N"),
                Dim));
        });

        var rng = new Random(2026);
        var buf = new float[Dim];

        _candidates = new VertexId[CandidateCount];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < CandidateCount; i++)
            {
                var n = tx.CreateVertex("N");
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                tx.SetVectorProperty(EntityRef.From(n), "embed", buf);
                _candidates[i] = n;
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
