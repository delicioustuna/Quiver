using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary>
///  sentinel: <see cref="FullTextScanOperator"/> BM25 top-K=10 over
/// 100 short docs sharing one query term. Corpus is left null so the operator
/// approximates N/avgdl from the norms index — the same path the text-first DSL
/// takes when GraphStats hasn't been collected.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FullTextScanOperatorBench
{
    private const string IndexName = "fts_bench";

    private string _dir = null!;
    private QuiverDatabase _db = null!;
    private IReadTransaction _readTx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = BenchTempDir.Create("ftscan");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        _db.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));

        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 100; i++)
            {
                var n = tx.CreateVertex("Doc");
                // Shared term "alpha" matches every doc; the rest varies the doc length
                // and df so BM25 has real work to rank.
                tx.SetProperty(n, "body",
                    PropertyValue.FromString($"alpha beta gamma doc number {i} unique{i:D4}"));
            }
            tx.Commit();
        }
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
    public int FullTextScan_k10()
    {
        using var op = new FullTextScanOperator(IndexName, "alpha", k: 10);
        return OperatorBenchDrain.Drain(op, _readTx);
    }
}
