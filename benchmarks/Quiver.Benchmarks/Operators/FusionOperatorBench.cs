using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary>
///  sentinel: <see cref="FusionOperator"/> RRF over a BM25 child
/// (<see cref="FullTextScanOperator"/>) and a vector child
/// (<see cref="KnnVertexSourceOperator"/>), each k=10, fused to k=10 — the shape
/// <c>g.HybridSearch</c> physicalizes to. Measures the fusion bookkeeping plus
/// both leaves, since the sentinel watches the whole hybrid path for regressions.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FusionOperatorBench
{
    private const int Dim = 16;
    private const string TextIndex = "fts_bench";
    private const string VectorIndex = "vec_bench";

    private string _dir = null!;
    private QuiverDatabase _db = null!;
    private IReadTransaction _readTx = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = BenchTempDir.Create("fusion");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        _db.EditSchema(schema => schema.CreateFullTextIndex(TextIndex, "Doc", "body"));
        _db.EditSchema(schema =>
        {
            schema.GetOrCreatePropertyKey("embed");
            schema.CreateIndex(new VectorIndexDefinition(
                VectorIndex,
                new PropertyTarget(PropertyOwnerKind.Vertex, "embed", "Doc"),
                Dim));
        });

        var rng = new Random(2026);
        var buf = new float[Dim];
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var n = tx.CreateVertex("Doc");
                tx.SetProperty(n, "body",
                    PropertyValue.FromString($"alpha beta gamma doc number {i} unique{i:D4}"));
                for (int d = 0; d < Dim; d++) buf[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
                tx.SetVectorProperty(EntityRef.From(n), "embed", buf);
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
    public int Fusion_bm25_knn_k10()
    {
        var text = new FullTextScanOperator(TextIndex, "alpha", k: 10);
        var knn = new KnnVertexSourceOperator(VectorIndex, _query, k: 10);
        using var op = new FusionOperator(new IPhysicalOperator[] { text, knn }, new[] { 0, 0 }, k: 10);
        return OperatorBenchDrain.Drain(op, _readTx);
    }
}
