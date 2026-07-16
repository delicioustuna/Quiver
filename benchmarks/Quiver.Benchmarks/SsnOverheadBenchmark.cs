using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// SSN (Serializable) を SI に対して上乗せしたときの per-tx オーバヘッドを測る。
///
/// <para>read-heavy (1000 read/tx) と write-heavy (100 write/tx) を SI / SSN で比較する。
/// いずれも単一スレッド・無競合 (commit は必ず成功) なので、計測されるのは SSN の
/// read/write set 収集 + commit 時 exclusion-window 検証 + post-commit スタンプ書き戻し
/// の純粋なコスト。</para>
///
/// <para>実行: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --filter '*SsnOverhead*'</c></para>
/// </summary>
[MemoryDiagnoser]
public class SsnOverheadBenchmark
{
    private const int VertexCount = 1_000;
    private const int ReadsPerTx = 1_000;
    private const int WritesPerTx = 100;

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private VertexId[] _ids = null!;
    private readonly Random _rng = new(42);

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("ssn_overhead");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _ids = new VertexId[VertexCount];
        using var tx = _db.BeginTransaction();
        for (int i = 0; i < VertexCount; i++)
        {
            _ids[i] = tx.CreateVertex("N");
            tx.SetProperty(_ids[i], "v", PropertyValue.FromInt64(i));
        }
        tx.Commit();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        BenchTempDir.Delete(_dbPath);
    }

    private long ReadHeavy(IsolationLevel level)
    {
        using var tx = _db.BeginTransaction(level);
        long sum = 0;
        for (int i = 0; i < ReadsPerTx; i++)
            sum += tx.GetProperty(_ids[_rng.Next(VertexCount)], "v").Int64Value;
        tx.Commit();
        return sum;
    }

    private void WriteHeavy(IsolationLevel level)
    {
        using var tx = _db.BeginTransaction(level);
        for (int i = 0; i < WritesPerTx; i++)
            tx.SetProperty(_ids[_rng.Next(VertexCount)], "v", PropertyValue.FromInt64(i));
        tx.Commit();
    }

    [Benchmark(Baseline = true, Description = "ReadHeavy SI (1000 read/tx)")]
    public long ReadHeavy_SI() => ReadHeavy(IsolationLevel.SnapshotIsolation);

    [Benchmark(Description = "ReadHeavy SSN (1000 read/tx)")]
    public long ReadHeavy_SSN() => ReadHeavy(IsolationLevel.Serializable);

    [Benchmark(Description = "WriteHeavy SI (100 write/tx)")]
    public void WriteHeavy_SI() => WriteHeavy(IsolationLevel.SnapshotIsolation);

    [Benchmark(Description = "WriteHeavy SSN (100 write/tx)")]
    public void WriteHeavy_SSN() => WriteHeavy(IsolationLevel.Serializable);
}
