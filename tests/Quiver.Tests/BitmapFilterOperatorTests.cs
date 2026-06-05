using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>PW-12: BitmapFilterOperator + PageSelectionBitmap.</summary>
public sealed class BitmapFilterOperatorTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public BitmapFilterOperatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_pw12_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class CountingPredicate : IPredicate
    {
        private readonly Func<NodeId, ITransaction, bool> _impl;
        public int Calls;
        public CountingPredicate(Func<NodeId, ITransaction, bool> impl) { _impl = impl; }
        public bool Evaluate(in TupleRef tuple, ITransaction tx)
        {
            Calls++;
            var nid = new NodeId(tuple[0].LongValue);
            return _impl(nid, tx);
        }
    }

    private void Seed(int total, int matchA, int matchB, int matchBoth)
    {
        // matchA: first 'matchA' nodes get tag=A. matchB: first 'matchB' nodes get hot=true.
        // matchBoth = min(matchA, matchB).
        using var tx = _db.BeginTransaction();
        _db.Schema.GetOrCreateLabel("N");
        for (int i = 0; i < total; i++)
        {
            var nid = tx.CreateNode("N");
            tx.SetProperty(nid, "tag", PropertyValue.FromString(i < matchA ? "A" : "B"));
            tx.SetProperty(nid, "hot", PropertyValue.FromBool(i < matchB));
        }
        tx.Commit();
    }

    [Fact]
    public void Yields_same_rows_as_chained_FilterOperator()
    {
        Seed(total: 200, matchA: 50, matchB: 80, matchBoth: 50);
        using var tx = _db.BeginTransaction();
        var label = _db.Schema.GetOrCreateLabel("N");
        var tagKey = _db.Schema.GetOrCreatePropertyKey("tag");
        var hotKey = _db.Schema.GetOrCreatePropertyKey("hot");

        IPredicate tagIsA = new TagEqPredicate(tagKey, "A");
        IPredicate hotIsTrue = new HotEqPredicate(hotKey, true);

        // Reference: chained FilterOperator.
        using var refResult = tx.Execute(
            new FilterOperator(
                new FilterOperator(new NodeByLabelScanOperator(label), tagIsA),
                hotIsTrue));
        var refIds = refResult.Rows().Select(r => r.GetNodeId(0).Value).OrderBy(v => v).ToList();

        // Bitmap variant.
        IPredicate tagIsA2 = new TagEqPredicate(tagKey, "A");
        IPredicate hotIsTrue2 = new HotEqPredicate(hotKey, true);
        using var bmResult = tx.Execute(
            new BitmapFilterOperator(
                new NodeByLabelScanOperator(label),
                new IPredicate[] { tagIsA2, hotIsTrue2 }));
        var bmIds = bmResult.Rows().Select(r => r.GetNodeId(0).Value).OrderBy(v => v).ToList();

        bmIds.Should().BeEquivalentTo(refIds);
        bmIds.Count.Should().Be(50); // matchBoth
        tx.Rollback();
    }

    [Fact]
    public void Most_selective_first_reduces_second_predicate_calls()
    {
        // 1000 nodes: 50 with tag=A, 500 with hot=true, 50 with both.
        Seed(total: 1000, matchA: 50, matchB: 500, matchBoth: 50);
        using var tx = _db.BeginTransaction();
        var label = _db.Schema.GetOrCreateLabel("N");
        var tagKey = _db.Schema.GetOrCreatePropertyKey("tag");
        var hotKey = _db.Schema.GetOrCreatePropertyKey("hot");

        var tagSelective = new CountingPredicate((nid, t) =>
        {
            using var h = t.Nodes.Read(nid);
            var en = t.Nodes.EnumerateProperties(nid, t.Properties); // ARCH-5c: inline + overflow
            while (en.MoveNext())
                if (en.Current.KeyId == tagKey)
                    return System.Text.Encoding.UTF8.GetString(en.Current.Value.Utf8StringValue) == "A";
            return false;
        });
        var hotBroad = new CountingPredicate((nid, t) =>
        {
            using var h = t.Nodes.Read(nid);
            var en = t.Nodes.EnumerateProperties(nid, t.Properties); // ARCH-5c: inline + overflow
            while (en.MoveNext())
                if (en.Current.KeyId == hotKey)
                    return en.Current.Value.BoolValue;
            return false;
        });

        // Selective first.
        using var result = tx.Execute(
            new BitmapFilterOperator(
                new NodeByLabelScanOperator(label),
                new IPredicate[] { tagSelective, hotBroad }));
        var ids = result.Rows().Select(r => r.GetNodeId(0).Value).ToList();
        ids.Count.Should().Be(50);

        // First predicate ran for every input row, second predicate only for survivors of first.
        tagSelective.Calls.Should().Be(1000);
        hotBroad.Calls.Should().Be(50);
        result.Statistics.PredicateEvaluations.Should().Be(1050);
        tx.Rollback();
    }

    [Fact]
    public void Least_selective_first_evaluates_more_predicates()
    {
        Seed(total: 1000, matchA: 50, matchB: 500, matchBoth: 50);
        using var tx = _db.BeginTransaction();
        var label = _db.Schema.GetOrCreateLabel("N");
        var tagKey = _db.Schema.GetOrCreatePropertyKey("tag");
        var hotKey = _db.Schema.GetOrCreatePropertyKey("hot");

        var tagSelective = new CountingPredicate((nid, t) =>
        {
            using var h = t.Nodes.Read(nid);
            var en = t.Nodes.EnumerateProperties(nid, t.Properties); // ARCH-5c: inline + overflow
            while (en.MoveNext())
                if (en.Current.KeyId == tagKey)
                    return System.Text.Encoding.UTF8.GetString(en.Current.Value.Utf8StringValue) == "A";
            return false;
        });
        var hotBroad = new CountingPredicate((nid, t) =>
        {
            using var h = t.Nodes.Read(nid);
            var en = t.Nodes.EnumerateProperties(nid, t.Properties); // ARCH-5c: inline + overflow
            while (en.MoveNext())
                if (en.Current.KeyId == hotKey)
                    return en.Current.Value.BoolValue;
            return false;
        });

        // Broad first.
        using var result = tx.Execute(
            new BitmapFilterOperator(
                new NodeByLabelScanOperator(label),
                new IPredicate[] { hotBroad, tagSelective }));
        var ids = result.Rows().Select(r => r.GetNodeId(0).Value).ToList();
        ids.Count.Should().Be(50);

        hotBroad.Calls.Should().Be(1000);
        tagSelective.Calls.Should().Be(500);
        result.Statistics.PredicateEvaluations.Should().Be(1500);
        tx.Rollback();
    }

    [Fact]
    public void Empty_source_yields_no_rows()
    {
        using var tx = _db.BeginTransaction();
        var label = _db.Schema.GetOrCreateLabel("Missing");

        var always = new CountingPredicate((_, _) => true);
        using var result = tx.Execute(
            new BitmapFilterOperator(
                new NodeByLabelScanOperator(label),
                new IPredicate[] { always }));
        result.Rows().Count().Should().Be(0);
        always.Calls.Should().Be(0);
        tx.Rollback();
    }

    [Fact]
    public void All_pass_yields_every_input_row()
    {
        Seed(total: 70, matchA: 70, matchB: 70, matchBoth: 70);
        using var tx = _db.BeginTransaction();
        var label = _db.Schema.GetOrCreateLabel("N");
        var alwaysTrue = new CountingPredicate((_, _) => true);
        using var result = tx.Execute(
            new BitmapFilterOperator(
                new NodeByLabelScanOperator(label),
                new IPredicate[] { alwaysTrue }));
        result.Rows().Count().Should().Be(70);
        alwaysTrue.Calls.Should().Be(70);
        tx.Rollback();
    }

    [Fact]
    public void All_fail_yields_zero_rows()
    {
        Seed(total: 70, matchA: 70, matchB: 70, matchBoth: 70);
        using var tx = _db.BeginTransaction();
        var label = _db.Schema.GetOrCreateLabel("N");
        var alwaysFalse = new CountingPredicate((_, _) => false);
        var alsoAlwaysFalse = new CountingPredicate((_, _) => false);
        using var result = tx.Execute(
            new BitmapFilterOperator(
                new NodeByLabelScanOperator(label),
                new IPredicate[] { alwaysFalse, alsoAlwaysFalse }));
        result.Rows().Count().Should().Be(0);
        alwaysFalse.Calls.Should().Be(70);
        // After predicate 1 cleared every row in each batch the bitmap is empty,
        // so predicate 2 should be skipped.
        alsoAlwaysFalse.Calls.Should().Be(0);
        tx.Rollback();
    }

    [Fact]
    public void Spans_multiple_batches_when_input_exceeds_batch_size()
    {
        // BatchSize = 64. Feed 200 rows so we cross batch boundaries.
        Seed(total: 200, matchA: 30, matchB: 200, matchBoth: 30);
        using var tx = _db.BeginTransaction();
        var label = _db.Schema.GetOrCreateLabel("N");
        var tagKey = _db.Schema.GetOrCreatePropertyKey("tag");

        var pickA = new CountingPredicate((nid, t) =>
        {
            using var h = t.Nodes.Read(nid);
            var en = t.Nodes.EnumerateProperties(nid, t.Properties); // ARCH-5c: inline + overflow
            while (en.MoveNext())
                if (en.Current.KeyId == tagKey)
                    return System.Text.Encoding.UTF8.GetString(en.Current.Value.Utf8StringValue) == "A";
            return false;
        });

        using var result = tx.Execute(
            new BitmapFilterOperator(
                new NodeByLabelScanOperator(label),
                new IPredicate[] { pickA }));
        result.Rows().Count().Should().Be(30);
        pickA.Calls.Should().Be(200);
        tx.Rollback();
    }

    [Fact]
    public void Constructor_rejects_empty_predicate_list()
    {
        Action act = () => _ = new BitmapFilterOperator(new AllNodesScanOperator(), Array.Empty<IPredicate>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void QueryOptimizer_orders_by_selectivity_ascending()
    {
        IPredicate p1 = new TagEqPredicate(new PropertyKeyId(1), "A");
        IPredicate p2 = new TagEqPredicate(new PropertyKeyId(2), "B");
        IPredicate p3 = new TagEqPredicate(new PropertyKeyId(3), "C");
        var ordered = QueryOptimizer.OrderPredicatesBySelectivity(new[]
        {
            new QueryOptimizer.PredicateCandidate(p1, 1000),
            new QueryOptimizer.PredicateCandidate(p2, 10),
            new QueryOptimizer.PredicateCandidate(p3, 100),
        });
        ordered.Should().Equal(p2, p3, p1);
    }

    [Fact]
    public void QueryOptimizer_preserves_input_order_on_ties()
    {
        IPredicate p1 = new TagEqPredicate(new PropertyKeyId(1), "A");
        IPredicate p2 = new TagEqPredicate(new PropertyKeyId(2), "B");
        var ordered = QueryOptimizer.OrderPredicatesBySelectivity(new[]
        {
            new QueryOptimizer.PredicateCandidate(p1, 50),
            new QueryOptimizer.PredicateCandidate(p2, 50),
        });
        ordered.Should().Equal(p1, p2);
    }

    [Fact]
    public void PageSelectionBitmap_SetAll_then_IsSet_for_each_index_below_count()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 70);
        bm.SetAll();
        for (int i = 0; i < 70; i++) bm.IsSet(i).Should().BeTrue($"bit {i}");
        bm.IsSet(70).Should().BeFalse();
        bm.PopCount().Should().Be(70);
    }

    [Fact]
    public void PageSelectionBitmap_AndEquals_clears_only_the_indexed_bit()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 64);
        bm.SetAll();
        bm.AndEquals(3, false);
        bm.AndEquals(10, false);
        bm.AndEquals(63, false);
        bm.IsSet(3).Should().BeFalse();
        bm.IsSet(10).Should().BeFalse();
        bm.IsSet(63).Should().BeFalse();
        bm.IsSet(4).Should().BeTrue();
        bm.PopCount().Should().Be(61);
    }

    [Fact]
    public void PageSelectionBitmap_AndEquals_keep_true_is_noop()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 8);
        bm.SetAll();
        bm.AndEquals(2, true);
        bm.PopCount().Should().Be(8);
    }

    [Fact]
    public void PageSelectionBitmap_Enumerator_yields_set_bits_ascending()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 70);
        bm.SetAll();
        bm.AndEquals(0, false);
        bm.AndEquals(5, false);
        bm.AndEquals(64, false);

        var seen = new List<int>();
        var en = bm.GetEnumerator();
        while (en.MoveNext()) seen.Add(en.Current);
        seen.Should().Equal(Enumerable.Range(0, 70).Where(i => i != 0 && i != 5 && i != 64));
    }
}

file sealed class TagEqPredicate : IPredicate
{
    private readonly PropertyKeyId _key;
    private readonly string _expected;
    public TagEqPredicate(PropertyKeyId key, string expected) { _key = key; _expected = expected; }
    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nid = new NodeId(tuple[0].LongValue);
        using var h = tx.Nodes.Read(nid);
        var en = tx.Nodes.EnumerateProperties(nid, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            if (en.Current.KeyId != _key) continue;
            if (en.Current.Value.Type != PropertyValueType.String) return false;
            return System.Text.Encoding.UTF8.GetString(en.Current.Value.Utf8StringValue) == _expected;
        }
        return false;
    }
}

file sealed class HotEqPredicate : IPredicate
{
    private readonly PropertyKeyId _key;
    private readonly bool _expected;
    public HotEqPredicate(PropertyKeyId key, bool expected) { _key = key; _expected = expected; }
    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nid = new NodeId(tuple[0].LongValue);
        using var h = tx.Nodes.Read(nid);
        var en = tx.Nodes.EnumerateProperties(nid, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            if (en.Current.KeyId != _key) continue;
            if (en.Current.Value.Type != PropertyValueType.Bool) return false;
            return en.Current.Value.BoolValue == _expected;
        }
        return false;
    }
}
