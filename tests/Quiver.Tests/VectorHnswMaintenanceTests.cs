using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ARCH-6 follow-up (① overwrite 再リンク / ② HNSW 物理削除 + 再構築):
/// SetVector の上書きは HNSW を新ベクトルへ張り直し、RemoveVector はグラフからノードを物理削除する。
/// 削除が蓄積するとグラフは自動再構築される。
/// </summary>
public sealed class VectorHnswMaintenanceTests : IDisposable
{
    private const string IndexName = "embed";
    private readonly string _dir;
    private readonly string _path;

    public VectorHnswMaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_arch6f_" + Guid.NewGuid().ToString("N"));
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

    private static (List<long> Ids, List<float> Scores) Knn(GraphDatabase db, float[] query, int k)
    {
        using var cur = db.Vectors.KnnSearch(IndexName, query, k);
        var ids = new List<long>();
        var scores = new List<float>();
        while (cur.MoveNext()) { ids.Add(cur.Current.EntityId); scores.Add(cur.Current.Score); }
        return (ids, scores);
    }

    [Fact]
    public void Overwrite_relinks_hnsw_to_new_vector()
    {
        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            4, DistanceMetric.Dot, "test", null));

        long a;
        using (var tx = db.BeginTransaction())
        {
            var na = tx.CreateNode("Doc");
            a = EntityRef.Sequence(na.Value);
            tx.SetVector(EntityKind.Node, na.Value, IndexName, new float[] { 1, 0, 0, 0 });
            // 何件かダミーを足してグラフを非自明にする。
            for (int i = 0; i < 5; i++)
            {
                var n = tx.CreateNode("Doc");
                tx.SetVector(EntityKind.Node, n.Value, IndexName, new float[] { 0, 0, 1, 0 });
            }
            tx.Commit();
        }

        // 上書き前: [1,0,0,0] 方向で A の score = 1。
        Knn(db, new float[] { 1, 0, 0, 0 }, 1).Scores[0].Should().BeApproximately(1f, 1e-5f);

        // A を [0,1,0,0] へ上書き (re-link)。
        using (var tx = db.BeginTransaction())
        {
            tx.SetVector(EntityKind.Node, a, IndexName, new float[] { 0, 1, 0, 0 });
            tx.Commit();
        }

        // 上書き後: [0,1,0,0] 方向で A が top・score = 1。古い [1,0,0,0] 方向では A の dot=0。
        var (idsNew, scoresNew) = Knn(db, new float[] { 0, 1, 0, 0 }, 1);
        idsNew[0].Should().Be(a);
        scoresNew[0].Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void RemoveVector_physically_deletes_from_graph_and_persists()
    {
        long keep, drop;
        using (var db = GraphDatabase.Open(_path))
        {
            db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
                4, DistanceMetric.Dot, "test", null));
            using var tx = db.BeginTransaction();
            var a = tx.CreateNode("Doc");
            var b = tx.CreateNode("Doc");
            keep = EntityRef.Sequence(a.Value);
            drop = EntityRef.Sequence(b.Value);
            tx.SetVector(EntityKind.Node, a.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.SetVector(EntityKind.Node, b.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.RemoveVector(EntityKind.Node, b.Value, IndexName);
            tx.Commit();
        }

        using (var db = GraphDatabase.Open(_path))
        {
            var (ids, _) = Knn(db, new float[] { 1, 0, 0, 0 }, 10);
            ids.Should().ContainSingle().Which.Should().Be(keep);
            ids.Should().NotContain(drop);
        }
    }

    [Fact]
    public void Graph_stays_correct_after_many_deletes_trigger_rebuild()
    {
        const int Dim = 16, N = 200, K = 5;
        var rng = new Random(2024);
        var vecs = new List<float[]>();

        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Cosine, "test", null));

        var nodeIds = new List<long>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                var n = tx.CreateNode("Doc");
                nodeIds.Add(n.Value);
                var v = RandomVec(rng, Dim);
                vecs.Add(v);
                db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, v);
            }
            tx.Commit();
        }

        // 後半 150 件を削除 → tombstone が生存を上回り自動 Rebuild が走る。
        using (var tx = db.BeginTransaction())
        {
            for (int i = 50; i < N; i++)
                tx.RemoveVector(EntityKind.Node, nodeIds[i], IndexName);
            tx.Commit();
        }

        // 残り 50 件 (seq 0..49) のうちクエリ自身が最近傍に出る (cosine 自己類似 = 1)。
        var (ids, _) = Knn(db, vecs[10], K);
        ids.Should().Contain(EntityRef.Sequence(nodeIds[10]));
        // 削除済みの seq は一切返らない。
        ids.Should().OnlyContain(id => id < 50);
    }

    [Fact]
    public void Recall_stays_high_under_repeated_reingestion_churn_without_rebuild()
    {
        // VEC-13: 頻繁な再取込 (削除→挿入の繰り返し) でグラフが断片化しないことを検証する。
        // tombstone が生存数を超えない構成 (Stable=300, 累計削除=200) にして自動 Rebuild を発火させず、
        // 削除時の近傍修復 (HealNeighborhood) だけで安定集合の self-query recall が保たれることを確かめる。
        const int Dim = 32, Stable = 300, Churn = 10, Cycles = 20, K = 10;
        var rng = new Random(777);

        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Cosine, "test", null));

        // 安定集合: 一度入れたら消さない。
        var stableIds = new List<long>();
        var stableVecs = new List<float[]>();
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < Stable; i++)
            {
                var n = tx.CreateNode("Doc");
                var v = RandomVec(rng, Dim);
                stableIds.Add(n.Value);
                stableVecs.Add(v);
                db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, v);
            }
            tx.Commit();
        }

        // churn: 毎サイクル Churn 件を作って即削除 (再取込で seq が増え tombstone が溜まる)。
        for (int c = 0; c < Cycles; c++)
        {
            var churnIds = new List<long>();
            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < Churn; i++)
                {
                    var n = tx.CreateNode("Doc");
                    churnIds.Add(n.Value);
                    db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, RandomVec(rng, Dim));
                }
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                foreach (var id in churnIds)
                    tx.RemoveVector(EntityKind.Node, id, IndexName);
                tx.Commit();
            }
        }

        // 安定集合の各ベクトルで self-query → 自分が top-K に出る割合 (recall) を測る。
        int found = 0;
        for (int i = 0; i < Stable; i++)
        {
            var (ids, _) = Knn(db, stableVecs[i], K);
            if (ids.Contains(EntityRef.Sequence(stableIds[i]))) found++;
        }
        double recall = (double)found / Stable;
        recall.Should().BeGreaterThan(0.95, "近傍修復で churn 後も高 recall を保つ (断片化していない)");
    }
}
