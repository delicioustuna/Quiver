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
    public void Schema_has_single_NodeId_column()
    {
        var op = new ApplyDyadicOperator(
            new FixedNodeListOperator(), 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 3, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.NodeId);
        op.Dispose();
    }

    [Fact]
    public void Empty_upstream_returns_no_results()
    {
        using var fx = CreateFixtureWithVectors(0, tag: "dyadic_empty");

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(); // no candidates
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 3, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(((GraphTransaction)tx).Inner);
        op.MoveNext().Should().BeFalse();
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Missing_vector_index_throws_VectorException()
    {
        using var fx = OperatorTestFixture.Open(tx =>
        {
            tx.CreateNode("A");
        }, tag: "dyadic_no_idx");

        using var tx = fx.Db.BeginTransaction();
        var n = fx.Db.BeginReadOnlyTransaction();
        // グラフから有効なノード ID を取得する。
        var source = new AllNodesScanOperator();
        var op = new ApplyDyadicOperator(
            source, 0, "nonexistent_index", [1f, 0f, 0f, 0f], null, 0,
            null, 3, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        n.Dispose();

        Action act = () => op.Open(((GraphTransaction)tx).Inner);
        act.Should().Throw<VectorException>().WithMessage("*does not exist*");
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Single_candidate_dot_product_returns_one_result()
    {
        NodeId nodeId = default;
        using var fx = CreateFixtureWithVectors(1, tag: "dyadic_single_dot",
            seedVectors: (db, ids) =>
            {
                nodeId = ids[0];
                db.Vectors.SetVector(EntityKind.Node, ids[0].Value, VecIndex,
                    [1f, 0f, 0f, 0f]);
            });

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(nodeId);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 3, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(((GraphTransaction)tx).Inner);
        op.MoveNext().Should().BeTrue();
        op.MoveNext().Should().BeFalse();
        op.Statistics.RowsProduced.Should().Be(1);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Dot_product_topk_ordering()
    {
        var ids = new NodeId[3];
        using var fx = CreateFixtureWithVectors(3, tag: "dyadic_dot_topk",
            seedVectors: (db, nodeIds) =>
            {
                Array.Copy(nodeIds, ids, 3);
                // [1,0,0,0] に対する内積が異なるベクトルを用意する。
                // node0: dot=0.5, node1: dot=1.0, node2: dot=0.3
                db.Vectors.SetVector(EntityKind.Node, nodeIds[0].Value, VecIndex,
                    [0.5f, 0f, 0f, 0f]);
                db.Vectors.SetVector(EntityKind.Node, nodeIds[1].Value, VecIndex,
                    [1.0f, 0f, 0f, 0f]);
                db.Vectors.SetVector(EntityKind.Node, nodeIds[2].Value, VecIndex,
                    [0.3f, 0f, 0f, 0f]);
            });

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 10, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(((GraphTransaction)tx).Inner);

        var results = OperatorCollect.Collect(op);
        results.Should().HaveCount(3);
        // スコアの降順なので、内積が最大のものを先頭にする。
        results[0].Should().Be(ids[1].Value, "node1 has dot=1.0");
        results[1].Should().Be(ids[0].Value, "node0 has dot=0.5");
        results[2].Should().Be(ids[2].Value, "node2 has dot=0.3");
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Cosine_similarity_returns_correct_ranking()
    {
        var ids = new NodeId[2];
        using var fx = CreateFixtureWithVectors(2, tag: "dyadic_cosine",
            seedVectors: (db, nodeIds) =>
            {
                Array.Copy(nodeIds, ids, 2);
                // node0 はクエリと同じ方向なのでコサイン類似度は 1.0。
                db.Vectors.SetVector(EntityKind.Node, nodeIds[0].Value, VecIndex,
                    [1f, 0f, 0f, 0f]);
                // node1 はクエリと直交するのでコサイン類似度は 0.0。
                db.Vectors.SetVector(EntityKind.Node, nodeIds[1].Value, VecIndex,
                    [0f, 1f, 0f, 0f]);
            });

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 10, WrapScorer(new CosineSimilarityOp()), typeof(CosineSimilarityOp));
        op.Open(((GraphTransaction)tx).Inner);

        var results = OperatorCollect.Collect(op);
        results.Should().HaveCount(2);
        results[0].Should().Be(ids[0].Value, "identical direction = highest cosine");
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Euclidean_distance_returns_correct_ranking()
    {
        var ids = new NodeId[2];
        using var fx = CreateFixtureWithVectors(2, tag: "dyadic_euclidean",
            seedVectors: (db, nodeIds) =>
            {
                Array.Copy(nodeIds, ids, 2);
                // node0 はクエリに近いためユークリッド距離が小さい。
                db.Vectors.SetVector(EntityKind.Node, nodeIds[0].Value, VecIndex,
                    [1f, 0f, 0f, 0f]);
                // node1 はクエリから遠いためユークリッド距離が大きい。
                db.Vectors.SetVector(EntityKind.Node, nodeIds[1].Value, VecIndex,
                    [0f, 0f, 0f, 1f]);
            });

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 10, WrapScorer(new EuclideanDistanceOp()), typeof(EuclideanDistanceOp));
        op.Open(((GraphTransaction)tx).Inner);

        var results = OperatorCollect.Collect(op);
        results.Should().HaveCount(2);
        // VectorKnnHeap はスコア降順に並べる。
        // ユークリッド距離は遠いほど値が大きいため、遠いノードが先頭になる。
        results[0].Should().Be(ids[1].Value, "farther node has higher Euclidean distance score");
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void K_limits_output_count()
    {
        var ids = new NodeId[5];
        using var fx = CreateFixtureWithVectors(5, tag: "dyadic_klimit",
            seedVectors: (db, nodeIds) =>
            {
                Array.Copy(nodeIds, ids, 5);
                for (int i = 0; i < 5; i++)
                {
                    var vec = new float[Dim];
                    vec[0] = (i + 1) * 0.1f;
                    db.Vectors.SetVector(EntityKind.Node, nodeIds[i].Value, VecIndex, vec);
                }
            });

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 2, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(((GraphTransaction)tx).Inner);

        var results = OperatorCollect.Collect(op);
        results.Should().HaveCount(2);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Statistics_tracks_rows_produced()
    {
        var ids = new NodeId[3];
        using var fx = CreateFixtureWithVectors(3, tag: "dyadic_stats",
            seedVectors: (db, nodeIds) =>
            {
                Array.Copy(nodeIds, ids, 3);
                for (int i = 0; i < 3; i++)
                {
                    var vec = new float[Dim];
                    vec[i % Dim] = 1f;
                    db.Vectors.SetVector(EntityKind.Node, nodeIds[i].Value, VecIndex, vec);
                }
            });

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(ids);
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 10, WrapScorer(new DotProductOp()), typeof(DotProductOp));
        op.Open(((GraphTransaction)tx).Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        op.Statistics.RowsProduced.Should().Be(count);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void NaN_score_throws_VectorException()
    {
        var ids = new NodeId[1];
        using var fx = CreateFixtureWithVectors(1, tag: "dyadic_nan",
            seedVectors: (db, nodeIds) =>
            {
                Array.Copy(nodeIds, ids, 1);
                db.Vectors.SetVector(EntityKind.Node, nodeIds[0].Value, VecIndex,
                    [1f, 0f, 0f, 0f]);
            });

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(ids);
        DyadicScoreFunc nanScorer = (a, b, r) => float.NaN;
        var op = new ApplyDyadicOperator(
            source, 0, VecIndex, [1f, 0f, 0f, 0f], null, 0,
            null, 3, nanScorer, typeof(NaNTestOp));

        Action act = () => op.Open(((GraphTransaction)tx).Inner);
        act.Should().Throw<VectorException>().WithMessage("*NaN*");
        op.Dispose();
        tx.Rollback();
    }

    /// <summary>
    /// <paramref name="nodeCount"/> 個のノードと FlatOnly ベクトルインデックスを持つ
    /// フィクスチャを作成する。
    /// <paramref name="seedVectors"/> を指定した場合は、ノードのコミット後にベクトルを設定する。
    /// </summary>
    private static OperatorTestFixture CreateFixtureWithVectors(
        int nodeCount,
        string tag,
        Action<GraphDatabase, NodeId[]>? seedVectors = null)
    {
        var ids = new NodeId[nodeCount];
        var fx = OperatorTestFixture.Open(tx =>
        {
            for (int i = 0; i < nodeCount; i++)
                ids[i] = tx.CreateNode("Sensor");
        }, tag: tag);

        if (nodeCount > 0 || seedVectors is not null)
        {
            var keyId = fx.Db.Schema.GetOrCreatePropertyKey(VecIndex);
            fx.Db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                VecIndex, EntityKind.Node, keyId, Dim,
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
