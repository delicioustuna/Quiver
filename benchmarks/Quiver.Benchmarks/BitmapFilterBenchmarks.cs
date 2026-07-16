using BenchmarkDotNet.Attributes;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// BitmapFilterOperator vs chained FilterOperator across selectivity
/// orderings. Same input data, three plans:
///   1. FilterOperator(P_selective) → FilterOperator(P_broad)   ← row-by-row baseline
///   2. BitmapFilterOperator([P_selective, P_broad])             ← bitmap, best ordering
///   3. BitmapFilterOperator([P_broad, P_selective])             ← bitmap, worst ordering
/// Verifies the eval-count reduction translates to wall-clock and surfaces
/// the batch-buffer overhead vs gain crossover.
/// </summary>
[MemoryDiagnoser]
public class BitmapFilterBenchmarks
{
    [Params(1_000, 10_000)]
    public int Total { get; set; }

    // matchSelective: fraction matching the selective predicate (e.g. 5%)
    [Params(0.05)]
    public double SelectiveFraction { get; set; }

    // matchBroad: fraction matching the broad predicate (e.g. 50%)
    [Params(0.5)]
    public double BroadFraction { get; set; }

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private IGraphTransaction _readTx = null!;
    private LabelId _label;
    private PropertyKeyId _tagKey;
    private PropertyKeyId _hotKey;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("pw12");
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));

        int matchA = (int)(Total * SelectiveFraction);
        int matchHot = (int)(Total * BroadFraction);

        using (var tx = _db.BeginTransaction())
        {
            _label = _db.Schema.GetOrCreateLabel("N");
            _tagKey = _db.Schema.GetOrCreatePropertyKey("tag");
            _hotKey = _db.Schema.GetOrCreatePropertyKey("hot");

            for (int i = 0; i < Total; i++)
            {
                var nid = tx.CreateVertex("N");
                tx.SetProperty(nid, "tag", PropertyValue.FromString(i < matchA ? "A" : "B"));
                tx.SetProperty(nid, "hot", PropertyValue.FromBool(i < matchHot));
            }
            tx.Commit();
        }

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

    private IPredicate TagEqA() => new BenchTagEqPredicate(_tagKey, "A");
    private IPredicate HotTrue() => new BenchHotEqPredicate(_hotKey, true);

    [Benchmark(Baseline = true, Description = "Chained FilterOperator (selective→broad)")]
    public long Chained_Selective_First()
    {
        var plan = new FilterOperator(
            new FilterOperator(new VertexByLabelScanOperator(_label), TagEqA()),
            HotTrue());
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "BitmapFilter (selective→broad)")]
    public long Bitmap_Selective_First()
    {
        var plan = new BitmapFilterOperator(
            new VertexByLabelScanOperator(_label),
            new IPredicate[] { TagEqA(), HotTrue() });
        using var result = _readTx.Execute(plan);
        return result.Statistics.PredicateEvaluations;
    }

    [Benchmark(Description = "BitmapFilter (broad→selective)")]
    public long Bitmap_Broad_First()
    {
        var plan = new BitmapFilterOperator(
            new VertexByLabelScanOperator(_label),
            new IPredicate[] { HotTrue(), TagEqA() });
        using var result = _readTx.Execute(plan);
        return result.Statistics.PredicateEvaluations;
    }

    [Benchmark(Description = "FilterOperator single predicate (overhead floor)")]
    public long Filter_Single()
    {
        var plan = new FilterOperator(new VertexByLabelScanOperator(_label), TagEqA());
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced;
    }

    [Benchmark(Description = "BitmapFilter single predicate (overhead floor)")]
    public long Bitmap_Single()
    {
        var plan = new BitmapFilterOperator(
            new VertexByLabelScanOperator(_label),
            new IPredicate[] { TagEqA() });
        using var result = _readTx.Execute(plan);
        return result.Statistics.PredicateEvaluations;
    }
}

file sealed class BenchTagEqPredicate : IPredicate
{
    private readonly PropertyKeyId _key;
    private readonly string _expected;
    public BenchTagEqPredicate(PropertyKeyId key, string expected) { _key = key; _expected = expected; }
    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nid = new VertexId(tuple[0].LongValue);
        using var h = tx.Vertices.Read(nid);
        var en = tx.Properties.Enumerate(EntityRef.From(nid), h.FirstPropertyRef);
        while (en.MoveNext())
        {
            if (en.Current.KeyId != _key) continue;
            if (en.Current.Value.Type != PropertyValueType.String) return false;
            return System.Text.Encoding.UTF8.GetString(en.Current.Value.Utf8StringValue) == _expected;
        }
        return false;
    }
}

file sealed class BenchHotEqPredicate : IPredicate
{
    private readonly PropertyKeyId _key;
    private readonly bool _expected;
    public BenchHotEqPredicate(PropertyKeyId key, bool expected) { _key = key; _expected = expected; }
    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nid = new VertexId(tuple[0].LongValue);
        using var h = tx.Vertices.Read(nid);
        var en = tx.Properties.Enumerate(EntityRef.From(nid), h.FirstPropertyRef);
        while (en.MoveNext())
        {
            if (en.Current.KeyId != _key) continue;
            if (en.Current.Value.Type != PropertyValueType.Bool) return false;
            return en.Current.Value.BoolValue == _expected;
        }
        return false;
    }
}
