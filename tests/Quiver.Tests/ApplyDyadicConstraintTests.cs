using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// Constraint enforcement matrix tests (design doc §4.5):
/// NaN score prohibition, exception containment, and gather/score
/// phase separation (concurrent write non-blocking).
/// </summary>
public sealed class ApplyDyadicConstraintTests : IDisposable
{
    private const string VecIndex = "Waveform";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public ApplyDyadicConstraintTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sig8_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.Schema.GetOrCreatePropertyKey(VecIndex);
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VecIndex, EntityKind.Node, keyId, Dim,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly));

        using var tx = _db.BeginTransaction();
        for (int i = 0; i < 5; i++)
        {
            var nid = tx.CreateNode("Sensor");
            tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
            var vec = new float[Dim];
            vec[i % Dim] = 1f;
            _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, vec);
        }
        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>§4.5 row 4: NaN score → VectorException with operator type name.</summary>
    [Fact]
    public void NaN_score_throws_VectorException()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () => g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<NaNOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        act.Should().Throw<VectorException>()
            .WithMessage("*NaN*")
            .WithMessage("*NaNOp*");
    }

    /// <summary>§4.5 row 5: operator exception → query abort, tx state unaffected.</summary>
    [Fact]
    public void Operator_exception_aborts_query_without_affecting_tx()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act = () => g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<ThrowingOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        act.Should().Throw<Exception>()
            .WithMessage("*deliberate*");

        // Transaction is still usable after the query exception
        var count = g.Nodes().HasLabel("Sensor").Count();
        count.Should().Be(5);
    }

    /// <summary>§4.5 row 3: gather/score separation — concurrent SetVector doesn't deadlock.</summary>
    [Fact]
    public async Task Concurrent_write_during_scoring_does_not_deadlock()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 20 && !cts.Token.IsCancellationRequested; i++)
            {
                using var tx = _db.BeginTransaction();
                var nid = tx.CreateNode("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("B"));
                _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex,
                    [0.1f * i, 0.2f, 0.3f, 0.4f]);
                tx.Commit();
            }
        }, cts.Token);

        // Reader: run ApplyDyadic scoring concurrently
        using (var rtx = _db.BeginReadOnlyTransaction())
        {
            var g = rtx.G(_db.Schema);
            var hits = g.Nodes<SensorNode>().Has(s => s.Site, "A")
                .ApplyDyadic<SlowOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 3)
                .ToList();

            hits.Should().HaveCountLessOrEqualTo(3);
        }

        await writerTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>±Infinity scores are permitted (order is well-defined).</summary>
    [Fact]
    public void Infinity_score_is_permitted()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var hits = g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<InfinityOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 3)
            .ToListWithIds();

        hits.Should().HaveCount(3);
    }

    // ── Custom operators for constraint testing ──

    private readonly struct NaNOp : IDyadicOperator<float>
    {
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions)
            => float.NaN;
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
            => float.NaN;
    }

    private readonly struct ThrowingOp : IDyadicOperator<float>
    {
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions)
            => throw new InvalidOperationException("deliberate test exception");
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
            => throw new InvalidOperationException("deliberate test exception");
    }

    private readonly struct SlowOp : IDyadicOperator<float>
    {
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions)
        {
            Thread.SpinWait(1000);
            return VectorScorer.Cosine(a, b);
        }
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
            => Invoke(a, b, ReadOnlySpan<Range>.Empty);
    }

    private readonly struct InfinityOp : IDyadicOperator<float>
    {
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions)
            => float.PositiveInfinity;
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
            => float.PositiveInfinity;
    }

    // ── Minimal IGraphNode types ──

    private sealed class SensorNode : IGraphNode<SensorNode>
    {
        public string Site { get; set; } = "";
        public float[] Waveform { get; set; } = [];

        public static string GraphLabel => "Sensor";

        public static NodeId Insert(IGraphTransaction tx, SensorNode entity)
        {
            var id = tx.CreateNode(GraphLabel);
            tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
            return id;
        }

        public static NodeId InsertIndexed(IGraphTransaction tx, SensorNode entity) => Insert(tx, entity);

        public static SensorNode Load(IGraphTransaction tx, NodeId id)
            => new()
            {
                Site = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Site").Utf8StringValue),
            };

        public static void Update(IGraphTransaction tx, NodeId id, SensorNode entity)
            => tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));

        public static void Delete(IGraphTransaction tx, NodeId id) => tx.DeleteNode(id);
    }
}
