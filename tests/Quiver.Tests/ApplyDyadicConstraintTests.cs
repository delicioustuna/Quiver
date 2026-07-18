using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 二項演算の制約を検証する。
/// NaN スコアの禁止、演算子例外の封じ込め、候補収集と採点の分離による
/// 並行書き込みの非ブロッキング性を確認する。
/// </summary>
public sealed class ApplyDyadicConstraintTests : IDisposable
{
    private const string VecIndex = "Waveform";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public ApplyDyadicConstraintTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sig8_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.EditSchema(schema => schema.GetOrCreatePropertyKey(VecIndex));
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VecIndex, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly));

        using var tx = _db.BeginWriteTransaction();
        for (int i = 0; i < 5; i++)
        {
            var nid = tx.CreateVertex("Sensor");
            tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
            var vec = new float[Dim];
            vec[i % Dim] = 1f;
            tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex, vec);
        }
        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>NaN スコアでは演算子の型名を含む <c>VectorException</c> を送出する。</summary>
    [Fact]
    public void NaN_score_throws_VectorException()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var act = () => g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<NaNOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        act.Should().Throw<VectorException>()
            .WithMessage("*NaN*")
            .WithMessage("*NaNOp*");
    }

    /// <summary>演算子例外でクエリだけを中断し、トランザクション状態は維持する。</summary>
    [Fact]
    public void Operator_exception_aborts_query_without_affecting_tx()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var act = () => g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<ThrowingOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 3)
            .ToList();

        act.Should().Throw<Exception>()
            .WithMessage("*deliberate*");

        // クエリ例外後もトランザクションを使用できる。
        var count = g.Vertices().HasLabel("Sensor").Count();
        count.Should().Be(5);
    }

    /// <summary>候補収集と採点を分離し、並行する SetVector とデッドロックしない。</summary>
    [Fact]
    public async Task Concurrent_write_during_scoring_does_not_deadlock()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 20 && !cts.Token.IsCancellationRequested; i++)
            {
                using var tx = _db.BeginWriteTransaction();
                var nid = tx.CreateVertex("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("B"));
                tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex,
                    [0.1f * i, 0.2f, 0.3f, 0.4f]);
                tx.Commit();
            }
        }, cts.Token);

        // リーダー側で ApplyDyadic の採点を並行実行する。
        using (var rtx = _db.BeginReadTransaction())
        {
            var g = rtx.Query;
            var hits = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
                .ApplyDyadic<SlowOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 3)
                .ToList();

            hits.Should().HaveCountLessOrEqualTo(3);
        }

        await writerTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>順序を定義できる正負の無限大スコアは許可する。</summary>
    [Fact]
    public void Infinity_score_is_permitted()
    {
        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        var hits = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<InfinityOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 3)
            .ToListWithIds();

        hits.Should().HaveCount(3);
    }

    // ── 制約テスト用のカスタム演算子 ──

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

    // ── 最小構成の IGraphVertex 型 ──

    private sealed class SensorVertex : IGraphVertex<SensorVertex>
    {
        public string Site { get; set; } = "";
        public float[] Waveform { get; set; } = [];

        public static string GraphLabel => "Sensor";

        public static VertexId Insert(IWriteTransaction tx, SensorVertex entity)
        {
            var id = tx.CreateVertex(GraphLabel);
            tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
            return id;
        }

        public static VertexId InsertIndexed(IWriteTransaction tx, SensorVertex entity) => Insert(tx, entity);

        public static SensorVertex Load(IReadTransaction tx, VertexId id)
            => new()
            {
                Site = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Site").Utf8StringValue),
            };

        public static void Update(IWriteTransaction tx, VertexId id, SensorVertex entity)
            => tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));

        public static void Delete(IWriteTransaction tx, VertexId id) => tx.DeleteVertex(id);
    }
}
