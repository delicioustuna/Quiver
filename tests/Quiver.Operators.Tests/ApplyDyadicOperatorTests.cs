using FluentAssertions;
using Quiver;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Query.Physical.Tests;

/// <summary>
/// <see cref="ApplyDyadicOperator"/> を単体で検証する。
/// DSL のトラバーサル層を介さず Open / MoveNext を直接呼び、
/// 組み込み二項演算子 (内積、コサイン、ユークリッド距離) の候補収集と採点を確認する。
/// </summary>
public sealed class ApplyDyadicOperatorTests
{
    private const string VecIndex = "vec_idx";
    private const int Dim = 4;

    /// <summary>構造体の演算子を <see cref="DyadicScoreFunc"/> デリゲートで包む。</summary>
    private static DyadicScoreFunc WrapScorer<T>(T op) where T : struct, IDyadicOperator<float>
        => (a, b, r) => op.Invoke(a, b, r);

    [Fact]
    public void Constructor_rejects_null_source()
    {
        Action act = () => new ApplyDyadicOperator(
            null!, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0, null, 3,
            WrapScorer(new DotProductOp()), typeof(DotProductOp));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Schema_has_single_VertexId_column()
    {
        var op = new ApplyDyadicOperator(
            new FixedVertexListOperator(), 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 3, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
        op.Dispose();
    }

    [Fact]
    public void Empty_upstream_returns_no_results()
    {
        using var fx = CreateFixtureWithVectors(0, tag: "dyadic_empty");

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(); // no candidates
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 3, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(tx.AsInternal().Inner);
        op.MoveNext().Should().BeFalse();
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Missing_vector_index_throws_VectorException()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateVertex("A");
        }, tag: "dyadic_no_idx");

        using var tx = fx.Db.BeginWriteTransaction();
        var n = fx.Db.BeginReadTransaction();
        // グラフから有効なVertex ID を取得する。
        var source = new AllVerticesScanOperator();
        var op = new ApplyDyadicOperator(
            source, 0, "nonexistent_index", [1f, 0f, 0f, 0f], null, 0,
            null, 3, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        n.Dispose();

        Action act = () => op.Open(tx.AsInternal().Inner);
        act.Should().Throw<VectorException>().WithMessage("*does not exist*");
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Single_candidate_dot_product_returns_one_result()
    {
        VertexId vertexId = default;
        using var fx = CreateFixtureWithVectors(1, tag: "dyadic_single_dot",
            seedVectors: (db, ids) =>
            {
                vertexId = ids[0];
                db.Vectors.SetVector(EntityKind.Vertex, ids[0].Value, VecIndex,
                    [1f, 0f, 0f, 0f]);
            });

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(vertexId);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 3, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(tx.AsInternal().Inner);
        op.MoveNext().Should().BeTrue();
        op.MoveNext().Should().BeFalse();
        op.Statistics.RowsProduced.Should().Be(1);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Dot_product_topk_ordering()
    {
        var ids = new VertexId[3];
        using var fx = CreateFixtureWithVectors(3, tag: "dyadic_dot_topk",
            seedVectors: (db, vertexIds) =>
            {
                Array.Copy(vertexIds, ids, 3);
                // [1,0,0,0] に対する内積が異なるベクトルを用意する。
                // vertex0: dot=0.5, vertex1: dot=1.0, vertex2: dot=0.3
                db.Vectors.SetVector(EntityKind.Vertex, vertexIds[0].Value, VecIndex,
                    [0.5f, 0f, 0f, 0f]);
                db.Vectors.SetVector(EntityKind.Vertex, vertexIds[1].Value, VecIndex,
                    [1.0f, 0f, 0f, 0f]);
                db.Vectors.SetVector(EntityKind.Vertex, vertexIds[2].Value, VecIndex,
                    [0.3f, 0f, 0f, 0f]);
            });

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 10, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(tx.AsInternal().Inner);

        var results = OperatorCollect.Collect(op);
        results.Should().HaveCount(3);
        // スコアの降順なので、内積が最大のものを先頭にする。
        results[0].Should().Be(ids[1].Value, "vertex1 has dot=1.0");
        results[1].Should().Be(ids[0].Value, "vertex0 has dot=0.5");
        results[2].Should().Be(ids[2].Value, "vertex2 has dot=0.3");
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Cosine_similarity_returns_correct_ranking()
    {
        var ids = new VertexId[2];
        using var fx = CreateFixtureWithVectors(2, tag: "dyadic_cosine",
            seedVectors: (db, vertexIds) =>
            {
                Array.Copy(vertexIds, ids, 2);
                // vertex0 はクエリと同じ方向なのでコサイン類似度は 1.0。
                db.Vectors.SetVector(EntityKind.Vertex, vertexIds[0].Value, VecIndex,
                    [1f, 0f, 0f, 0f]);
                // vertex1 はクエリと直交するのでコサイン類似度は 0.0。
                db.Vectors.SetVector(EntityKind.Vertex, vertexIds[1].Value, VecIndex,
                    [0f, 1f, 0f, 0f]);
            });

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 10, WrapScorer(new CosineSimilarityOp()), typeof(CosineSimilarityOp));
        op.Open(tx.AsInternal().Inner);

        var results = OperatorCollect.Collect(op);
        results.Should().HaveCount(2);
        results[0].Should().Be(ids[0].Value, "identical direction = highest cosine");
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Euclidean_distance_returns_correct_ranking()
    {
        var ids = new VertexId[2];
        using var fx = CreateFixtureWithVectors(2, tag: "dyadic_euclidean",
            seedVectors: (db, vertexIds) =>
            {
                Array.Copy(vertexIds, ids, 2);
                // vertex0 はクエリに近いためユークリッド距離が小さい。
                db.Vectors.SetVector(EntityKind.Vertex, vertexIds[0].Value, VecIndex,
                    [1f, 0f, 0f, 0f]);
                // vertex1 はクエリから遠いためユークリッド距離が大きい。
                db.Vectors.SetVector(EntityKind.Vertex, vertexIds[1].Value, VecIndex,
                    [0f, 0f, 0f, 1f]);
            });

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 10, WrapScorer(new EuclideanDistanceOp()), typeof(EuclideanDistanceOp));
        op.Open(tx.AsInternal().Inner);

        var results = OperatorCollect.Collect(op);
        results.Should().HaveCount(2);
        // VectorKnnHeap はスコア降順に並べる。
        // ユークリッド距離は遠いほど値が大きいため、遠いVertexが先頭になる。
        results[0].Should().Be(ids[1].Value, "farther vertex has higher Euclidean distance score");
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void K_limits_output_count()
    {
        var ids = new VertexId[5];
        using var fx = CreateFixtureWithVectors(5, tag: "dyadic_klimit",
            seedVectors: (db, vertexIds) =>
            {
                Array.Copy(vertexIds, ids, 5);
                for (int i = 0; i < 5; i++)
                {
                    var vec = new float[Dim];
                    vec[0] = (i + 1) * 0.1f;
                    db.Vectors.SetVector(EntityKind.Vertex, vertexIds[i].Value, VecIndex, vec);
                }
            });

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 2, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(tx.AsInternal().Inner);

        var results = OperatorCollect.Collect(op);
        results.Should().HaveCount(2);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Statistics_tracks_rows_produced()
    {
        var ids = new VertexId[3];
        using var fx = CreateFixtureWithVectors(3, tag: "dyadic_stats",
            seedVectors: (db, vertexIds) =>
            {
                Array.Copy(vertexIds, ids, 3);
                for (int i = 0; i < 3; i++)
                {
                    var vec = new float[Dim];
                    vec[i % Dim] = 1f;
                    db.Vectors.SetVector(EntityKind.Vertex, vertexIds[i].Value, VecIndex, vec);
                }
            });

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 10, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(tx.AsInternal().Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        op.Statistics.RowsProduced.Should().Be(count);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void NaN_score_throws_VectorException()
    {
        var ids = new VertexId[1];
        using var fx = CreateFixtureWithVectors(1, tag: "dyadic_nan",
            seedVectors: (db, vertexIds) =>
            {
                Array.Copy(vertexIds, ids, 1);
                db.Vectors.SetVector(EntityKind.Vertex, vertexIds[0].Value, VecIndex,
                    [1f, 0f, 0f, 0f]);
            });

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(ids);
        DyadicScoreFunc nanScorer = (a, b, r) => float.NaN;
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 3, nanScorer, typeof(NaNTestOp));

        Action act = () => op.Open(tx.AsInternal().Inner);
        act.Should().Throw<VectorException>().WithMessage("*NaN*");
        op.Dispose();
        tx.Rollback();
    }

    /// <summary>
    /// <paramref name="vertexCount"/> 個のVertexと FlatOnly ベクトルインデックスを持つ
    /// フィクスチャを作成する。
    /// <paramref name="seedVectors"/> を指定した場合は、Vertexのコミット後にベクトルを設定する。
    /// </summary>
    private static OperatorTestFixture CreateFixtureWithVectors(
        int vertexCount,
        string tag,
        Action<QuiverDatabase, VertexId[]>? seedVectors = null)
    {
        var ids = new VertexId[vertexCount];
        var fx = OperatorTestFixture.Open(tx =>
        {
            for (int i = 0; i < vertexCount; i++)
                ids[i] = tx.CreateVertex("Sensor");
        }, tag: tag);

        if (vertexCount > 0 || seedVectors is not null)
        {
            var keyId = fx.EditSchema(schema => schema.GetOrCreatePropertyKey(VecIndex));
            fx.Db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                VecIndex, EntityKind.Vertex, keyId, Dim,
                DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly));
        }

        seedVectors?.Invoke(fx.Db, ids);
        return fx;
    }

    /// <summary>NaN スコアの例外メッセージを検証するためのマーカー型。</summary>
    private readonly struct NaNTestOp : IDyadicOperator<float>
    {
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions)
            => float.NaN;
        public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
            => float.NaN;
    }
}
