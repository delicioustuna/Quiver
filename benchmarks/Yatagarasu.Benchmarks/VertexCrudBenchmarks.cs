using BenchmarkDotNet.Attributes;
using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// 目標: 点検索 warm cache &lt; 5µs、Insert &lt; 20µs
/// </summary>
[MemoryDiagnoser]
public class VertexCrudBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int VertexCount { get; set; }

    private YatagarasuDatabase _db = null!;
    private string _dbPath = null!;
    private VertexId[] _vertexIds = null!;
    private IReadTransaction _readTx = null!;
    private readonly Random _rng = new(42);

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("crud");
        _db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.yata"));
        _vertexIds = new VertexId[VertexCount];

        const int BatchSize = 5_000;
        for (int i = 0; i < VertexCount; i += BatchSize)
        {
            using var tx = _db.BeginWriteTransaction();
            int end = Math.Min(i + BatchSize, VertexCount);
            for (int j = i; j < end; j++)
                _vertexIds[j] = tx.CreateVertex("Vertex");
            tx.Commit();
        }

        _readTx = _db.BeginWriteTransaction();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        // 残骸蓄積の原因と対策は BenchTempDir 参照。
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Description = "VertexExists (warm)")]
    public bool LookupVertex()
    {
        var id = _vertexIds[_rng.Next(_vertexIds.Length)];
        return _readTx.VertexExists(id);
    }

    [Benchmark(Description = "CreateVertex + Commit")]
    public long InsertVertex()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("Vertex");
        tx.Commit();
        return id.Value;
    }

    [Benchmark(Description = "CreateVertex + SetProperty + Commit")]
    public long InsertVertexWithProperty()
    {
        using var tx = _db.BeginWriteTransaction();
        var id = tx.CreateVertex("Vertex");
        tx.SetProperty(id, "id", PropertyValue.FromInt64(id.Value));
        tx.Commit();
        return id.Value;
    }
}
