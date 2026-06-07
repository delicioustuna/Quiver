using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ARCH-6 (6b): SetVector の tx 統合。tx 配下のベクトル書き込みはグラフ変更と同じ container WAL に
/// 乗り、commit で原子確定 / abort で巻き戻る。db.Vectors の tx 外呼び出しは autocommit で永続化される。
/// </summary>
public sealed class VectorTransactionTests : IDisposable
{
    private const string IndexName = "embed";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly string _path;

    public VectorTransactionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_arch6b_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static VectorIndexSpec Spec(GraphDatabase db) =>
        new(IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("title"),
            Dim, DistanceMetric.Dot, "test", null);

    private static List<long> Knn(GraphDatabase db, float[] query, int k)
    {
        using var cur = db.Vectors.KnnSearch(IndexName, query, k);
        var got = new List<long>();
        while (cur.MoveNext()) got.Add(cur.Current.EntityId);
        return got;
    }

    [Fact]
    public void SetVector_rolls_back_with_graph_changes_on_abort()
    {
        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(Spec(db));

        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateNode("Doc");
            tx.SetVector(EntityKind.Node, n.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.Rollback();
        }

        // abort によりベクトルもグラフ変更も巻き戻り、KNN は空になる。
        Knn(db, new float[] { 1, 0, 0, 0 }, 10).Should().BeEmpty();
    }

    [Fact]
    public void Hybrid_graph_and_vector_commit_is_atomic_across_reopen()
    {
        long id;
        using (var db = GraphDatabase.Open(_path))
        {
            db.Vectors.CreateVectorIndex(Spec(db));
            using var tx = db.BeginTransaction();
            var n = tx.CreateNode("Doc");
            id = n.Value;
            tx.SetVector(EntityKind.Node, n.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.Commit();
        }

        using (var db = GraphDatabase.Open(_path))
        {
            // ノードもベクトルも一緒に永続化されている。
            using var rtx = db.BeginReadOnlyTransaction();
            rtx.NodeExists(new NodeId(id)).Should().BeTrue();

            Knn(db, new float[] { 1, 0, 0, 0 }, 10).Should().ContainSingle()
                .Which.Should().Be(EntityRef.Sequence(id));
        }
    }

    [Fact]
    public void Autocommit_SetVector_persists_without_explicit_tx()
    {
        long seq;
        using (var db = GraphDatabase.Open(_path))
        {
            db.Vectors.CreateVectorIndex(Spec(db));
            // tx を一切張らずに直接 SetVector → autocommit で crash-atomic に永続化される。
            NodeId n;
            using (var tx = db.BeginTransaction()) { n = tx.CreateNode("Doc"); tx.Commit(); }
            seq = EntityRef.Sequence(n.Value);
            db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, new float[] { 0, 1, 0, 0 });
        }

        using (var db = GraphDatabase.Open(_path))
        {
            Knn(db, new float[] { 0, 1, 0, 0 }, 10).Should().ContainSingle()
                .Which.Should().Be(seq);
        }
    }

    [Fact]
    public void SetVector_in_readonly_tx_throws()
    {
        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(Spec(db));
        using var rtx = db.BeginReadOnlyTransaction();
        var act = () => rtx.SetVector(EntityKind.Node, 0, IndexName, new float[] { 1, 0, 0, 0 });
        act.Should().Throw<InvalidOperationException>();
    }
}
