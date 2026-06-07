using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ARCH-6 (6c): ベクトル binding の世代照合。SetVector はバインド先の現世代を payload に焼き込み、
/// KNN read は現在の slot 世代と照合する。slot が解放→再利用され別エンティティに化けた場合
/// (= 世代 bump)、旧ベクトルは stale として KNN から除外される。
/// </summary>
public sealed class VectorGenerationBindingTests : IDisposable
{
    private const string IndexName = "embed";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly string _path;

    public VectorGenerationBindingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_arch6c_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static List<long> Knn(GraphDatabase db, float[] query, int k)
    {
        using var cur = db.Vectors.KnnSearch(IndexName, query, k);
        var got = new List<long>();
        while (cur.MoveNext()) got.Add(cur.Current.EntityId);
        return got;
    }

    [Fact]
    public void Stale_binding_is_rejected_after_slot_reuse()
    {
        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Dot, "test", null));

        // A を作りベクトルを焼く。
        NodeId a;
        long seqA;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("Doc");
            seqA = EntityRef.Sequence(a.Value);
            tx.SetVector(EntityKind.Node, a.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.Commit();
        }

        // A を削除 → vacuum で物理回収すると slot が free list に戻る (MVCC は即時には free しない)。
        using (var tx = db.BeginTransaction()) { tx.DeleteNode(a); tx.Commit(); }
        db.Vacuum();

        // 新ノード B を作る。回収済み slot を再利用し世代が bump する。B はベクトルを設定しない。
        NodeId b;
        using (var tx = db.BeginTransaction()) { b = tx.CreateNode("Doc"); tx.Commit(); }

        // 前提: 同一 Sequence が再利用された (世代だけ違う)。
        EntityRef.Sequence(b.Value).Should().Be(seqA);

        // A の旧ベクトルは stale なので KNN から除外される。
        Knn(db, new float[] { 1, 0, 0, 0 }, 10).Should().BeEmpty();
    }

    [Fact]
    public void Fresh_binding_on_reused_slot_is_returned()
    {
        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Dot, "test", null));

        NodeId a;
        using (var tx = db.BeginTransaction())
        {
            a = tx.CreateNode("Doc");
            tx.SetVector(EntityKind.Node, a.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.Commit();
        }
        using (var tx = db.BeginTransaction()) { tx.DeleteNode(a); tx.Commit(); }
        db.Vacuum();

        // B が同一 slot を再利用し、今度は自分のベクトルを焼く → 現世代でバインドされ live。
        NodeId b;
        using (var tx = db.BeginTransaction())
        {
            b = tx.CreateNode("Doc");
            tx.SetVector(EntityKind.Node, b.Value, IndexName, new float[] { 0, 1, 0, 0 });
            tx.Commit();
        }
        EntityRef.Sequence(b.Value).Should().Be(EntityRef.Sequence(a.Value));

        // 旧クエリ (A 方向) はもうヒットしない。新クエリ (B 方向) は B を返す。
        Knn(db, new float[] { 1, 0, 0, 0 }, 10).Should().ContainSingle()
            .Which.Should().Be(EntityRef.Sequence(b.Value)); // Dot: B も [0,1,0,0]·[1,0,0,0]=0 だが唯一の live
        Knn(db, new float[] { 0, 1, 0, 0 }, 10).Should().ContainSingle()
            .Which.Should().Be(EntityRef.Sequence(b.Value));
    }
}
