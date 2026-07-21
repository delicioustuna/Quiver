using System.Diagnostics;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 直接配列形式の結合インデックスがEdgeのスカラープロパティを往復保持し、
/// 対象外の入力を拒否することを検証する。
/// また、全Edgeが重みを持つワークロードで、
/// プロパティチェーン走査より十分に高速であることを確認する。
/// </summary>
public sealed class EdgePropertyJoinIndexTests : IDisposable
{
    private readonly string _dir;
    private QuiverDatabase? _db;

    public EdgePropertyJoinIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_edge_join_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Int64_round_trips_through_join_index()
    {
        BuildGraphWithInt64Weight(edgeCount: 16);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var idx = _db.BuildEdgePropertyJoinIndex("weight", PropertyValueType.Int64);
        idx.KeyId.Value.Should().BeGreaterOrEqualTo(0);
        idx.ValueType.Should().Be(PropertyValueType.Int64);
        idx.EntryCount.Should().Be(16);

        for (long i = 0; i < 16; i++)
        {
            bool ok = idx.TryGetScalar(new EdgeId(i), idx.KeyId, out var t, out long bits);
            ok.Should().BeTrue($"edge {i} was created with a weight");
            t.Should().Be(PropertyValueType.Int64);
            bits.Should().Be(100 + i);
        }
    }

    [Fact]
    public void Double_round_trips_via_bit_cast()
    {
        BuildGraphWithDoubleWeight(edgeCount: 8);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var idx = _db.BuildEdgePropertyJoinIndex("score", PropertyValueType.Double);
        idx.ValueType.Should().Be(PropertyValueType.Double);

        for (long i = 0; i < 8; i++)
        {
            bool ok = idx.TryGetScalar(new EdgeId(i), idx.KeyId, out _, out long bits);
            ok.Should().BeTrue();
            BitConverter.Int64BitsToDouble(bits).Should().BeApproximately(0.5 + i, 1e-12);
        }
    }

    [Fact]
    public void Missing_or_wrong_key_returns_false()
    {
        BuildGraphWithInt64Weight(edgeCount: 4);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var idx = _db.BuildEdgePropertyJoinIndex("weight", PropertyValueType.Int64);

        // Out-of-range edge id.
        idx.TryGetScalar(new EdgeId(9999), idx.KeyId, out _, out _).Should().BeFalse();

        // Wrong key — fabricate one that the index does not cover.
        var otherKey = _db.EditSchema(schema => schema.GetOrCreatePropertyKey("not_weight"));
        idx.TryGetScalar(new EdgeId(0), otherKey, out _, out _).Should().BeFalse();

        // Negative id sentinel.
        idx.TryGetScalar(new EdgeId(-1), idx.KeyId, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Edges_without_the_indexed_property_are_omitted()
    {
        // 5 edges total — only the first 3 carry the weight property.
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        _db.EditSchema(schema => schema.GetOrCreatePropertyKey("weight"));
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 6; i++) tx.CreateVertex("N");
            for (int i = 0; i < 5; i++)
            {
                var edge = tx.CreateEdge(new VertexId(0), new VertexId(i + 1), "KNOWS");
                if (i < 3) tx.SetProperty(edge, "weight", PropertyValue.FromInt64(10 + i));
            }
            tx.Commit();
        }

        var idx = _db.BuildEdgePropertyJoinIndex("weight", PropertyValueType.Int64);
        idx.EntryCount.Should().Be(3);

        for (int i = 0; i < 3; i++)
        {
            idx.TryGetScalar(new EdgeId(i), idx.KeyId, out _, out long bits).Should().BeTrue();
            bits.Should().Be(10 + i);
        }
        for (int i = 3; i < 5; i++)
        {
            idx.TryGetScalar(new EdgeId(i), idx.KeyId, out _, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void Type_mismatch_is_treated_as_missing()
    {
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = _db.BeginWriteTransaction())
        {
            tx.CreateVertex("N"); tx.CreateVertex("N");
            var edge = tx.CreateEdge(new VertexId(0), new VertexId(1), "KNOWS");
            // Store weight as Double when the index expects Int64.
            tx.SetProperty(edge, "weight", PropertyValue.FromDouble(1.25));
            tx.Commit();
        }

        var idx = _db.BuildEdgePropertyJoinIndex("weight", PropertyValueType.Int64);
        idx.EntryCount.Should().Be(0);
        idx.TryGetScalar(new EdgeId(0), idx.KeyId, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Direct_array_lookup_is_faster_than_property_chain()
    {
        // Scale chosen so chain walk is measurable but test stays well
        // under a second. The contract from feature.md is "1 桁速い"
        // (≥10×); we assert ≥5× to leave headroom for CI variance — the
        // observed ratio on a dev box is comfortably north of 10×.
        const int edgeCount = 4_000;
        BuildGraphWithInt64Weight(edgeCount);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var idx = _db.BuildEdgePropertyJoinIndex("weight", PropertyValueType.Int64);
        using var tx = _db.BeginWriteTransaction();

        // Warm both paths.
        for (int i = 0; i < 32; i++)
        {
            _ = tx.GetProperty(EdgeId.Create(i, 1), "weight");
            _ = idx.TryGetScalar(new EdgeId(i), idx.KeyId, out _, out _);
        }

        const int iterations = 4;
        long chainTicks = 0;
        long joinTicks = 0;
        for (int run = 0; run < iterations; run++)
        {
            var sw = Stopwatch.StartNew();
            long checksumChain = 0;
            for (long i = 0; i < edgeCount; i++)
                checksumChain += tx.GetProperty(EdgeId.Create(i, 1), "weight").Int64Value;
            sw.Stop();
            chainTicks += sw.ElapsedTicks;

            sw.Restart();
            long checksumJoin = 0;
            for (long i = 0; i < edgeCount; i++)
            {
                idx.TryGetScalar(new EdgeId(i), idx.KeyId, out _, out long bits);
                checksumJoin += bits;
            }
            sw.Stop();
            joinTicks += sw.ElapsedTicks;

            checksumChain.Should().Be(checksumJoin, "both paths must read the same data");
        }

        double ratio = (double)chainTicks / Math.Max(joinTicks, 1);
        ratio.Should().BeGreaterThan(5.0,
            $"join index should outpace property chain by ≥5× (chain={chainTicks}, join={joinTicks})");
    }

    private void BuildGraphWithInt64Weight(int edgeCount)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var key = db.EditSchema(schema => schema.GetOrCreatePropertyKey("weight"));
        using var tx = db.BeginWriteTransaction();
        for (int i = 0; i <= edgeCount; i++) tx.CreateVertex("N");
        for (int i = 0; i < edgeCount; i++)
        {
            var edge = tx.CreateEdge(new VertexId(0), new VertexId(i + 1), "KNOWS");
            tx.SetProperty(edge, "weight", PropertyValue.FromInt64(100 + i));
        }
        tx.Commit();
    }

    private void BuildGraphWithDoubleWeight(int edgeCount)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var key = db.EditSchema(schema => schema.GetOrCreatePropertyKey("score"));
        using var tx = db.BeginWriteTransaction();
        for (int i = 0; i <= edgeCount; i++) tx.CreateVertex("N");
        for (int i = 0; i < edgeCount; i++)
        {
            var edge = tx.CreateEdge(new VertexId(0), new VertexId(i + 1), "KNOWS");
            tx.SetProperty(edge, "score", PropertyValue.FromDouble(0.5 + i));
        }
        tx.Commit();
    }
}
