using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ARCH-6 (6d): 永続 HNSW ANN 索引。グラフはページに永続化され再起動を跨いで再現する。
/// 検索は ANN 化され、brute-force (KnnSearchBatch = flat scan) に対し高い recall を持つ。
/// グラフ書き込みは container WAL に乗るので abort で巻き戻る。
/// </summary>
public sealed class VectorHnswTests : IDisposable
{
    private const string IndexName = "embed";
    private readonly string _dir;
    private readonly string _path;

    public VectorHnswTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_arch6d_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static float[] RandomVec(Random rng, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() * 2 - 1);
        return v;
    }

    private static List<long> TopK(VectorSearchCursor cur, int k)
    {
        using (cur)
        {
            var got = new List<long>();
            while (cur.MoveNext() && got.Count < k) got.Add(cur.Current.EntityId);
            return got;
        }
    }

    [Fact]
    public void Hnsw_recall_is_high_versus_bruteforce()
    {
        const int Dim = 32, N = 1000, K = 10, Queries = 20;
        var rng = new Random(12345);

        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Cosine, "test", null));

        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                var n = tx.CreateNode("Doc");
                db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, RandomVec(rng, Dim));
            }
            tx.Commit();
        }

        double totalRecall = 0;
        for (int qi = 0; qi < Queries; qi++)
        {
            var q = RandomVec(rng, Dim);
            var hnsw = TopK(db.Vectors.KnnSearch(IndexName, q, K), K);
            // brute-force 基準 = flat scan (KnnSearchBatch)。
            var brute = TopK(db.Vectors.KnnSearchBatch(
                IndexName, new[] { (ReadOnlyMemory<float>)q.AsMemory() }, K)[0], K);

            int overlap = hnsw.Count(x => brute.Contains(x));
            totalRecall += overlap / (double)K;
        }
        (totalRecall / Queries).Should().BeGreaterThanOrEqualTo(0.85);
    }

    [Fact]
    public void Hnsw_graph_survives_close_and_reopen()
    {
        const int Dim = 16, N = 200, K = 5;
        var rng = new Random(777);
        var vectors = new List<float[]>();

        using (var db = GraphDatabase.Open(_path))
        {
            db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
                Dim, DistanceMetric.Cosine, "test", null));
            using var tx = db.BeginTransaction();
            for (int i = 0; i < N; i++)
            {
                var n = tx.CreateNode("Doc");
                var v = RandomVec(rng, Dim);
                vectors.Add(v);
                db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, v);
            }
            tx.Commit();
        }

        // reopen 後、HNSW グラフがページから再構築され KNN が同じ結果を返す。
        using (var db = GraphDatabase.Open(_path))
        {
            var q = vectors[42];
            var top = TopK(db.Vectors.KnnSearch(IndexName, q, K), K);
            // クエリ自身 (seq=42) が最近傍に含まれる (cosine 自己類似 = 1)。
            top.Should().Contain(EntityRef.Sequence(42));
        }
    }

    [Fact]
    public void Hnsw_insert_rolls_back_on_abort()
    {
        const int Dim = 8;
        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Dot, "test", null));

        // committed な 1 件。
        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateNode("Doc");
            db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, new float[] { 1, 0, 0, 0, 0, 0, 0, 0 });
            tx.Commit();
        }

        // abort する tx で別ベクトルを挿入 → 巻き戻る。
        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateNode("Doc");
            tx.SetVector(EntityKind.Node, n.Value, IndexName, new float[] { 0, 1, 0, 0, 0, 0, 0, 0 });
            tx.Rollback();
        }

        // [0,1,..] クエリは abort された 2 件目を返さない。committed な 1 件目 (dot=0) のみ。
        var top = TopK(db.Vectors.KnnSearch(IndexName, new float[] { 0, 1, 0, 0, 0, 0, 0, 0 }, 10), 10);
        top.Should().ContainSingle().Which.Should().Be(0L);
    }
}
