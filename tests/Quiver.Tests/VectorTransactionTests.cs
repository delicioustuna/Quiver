using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <c>SetVector</c> とトランザクションの統合を検証する。
/// トランザクション内のベクトル書き込みはグラフ変更と同じコンテナ WAL に記録され、
/// コミット時に原子的に確定し、中断時に巻き戻る。
/// <c>db.Vectors</c> をトランザクション外から呼ぶ場合は自動コミットで永続化される。
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

    private static VectorIndexSpec Spec(QuiverDatabase db) =>
        new(IndexName, EntityKind.Vertex, db.Schema.GetOrCreatePropertyKey("title"),
            Dim, DistanceMetric.Dot, "test", null);

    private static List<long> Knn(QuiverDatabase db, float[] query, int k)
    {
        using var cur = db.Vectors.KnnSearch(IndexName, query, k);
        var got = new List<long>();
        while (cur.MoveNext()) got.Add(cur.Current.EntityId);
        return got;
    }

    [Fact]
    public void SetVector_rolls_back_with_graph_changes_on_abort()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(Spec(db));

        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateVertex("Doc");
            tx.SetVector(EntityKind.Vertex, n.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.Rollback();
        }

        // abort によりベクトルもグラフ変更も巻き戻り、KNN は空になる。
        Knn(db, new float[] { 1, 0, 0, 0 }, 10).Should().BeEmpty();
    }

    [Fact]
    public void Aborted_overwrite_invalidates_payload_cache()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(Spec(db));
        VertexId vertex;
        using (var tx = db.BeginTransaction())
        {
            vertex = tx.CreateVertex("Doc");
            tx.SetVector(EntityKind.Vertex, vertex.Value, IndexName, [1f, 0f, 0f, 0f]);
            tx.Commit();
        }

        // committed 値を slab に載せてから、同じ seq を未 commit 値で write-through する。
        var destination = new float[Dim];
        db.Vectors.TryGetVector(EntityKind.Vertex, vertex.Value, IndexName, destination).Should().BeTrue();
        destination.Should().Equal(1f, 0f, 0f, 0f);

        using (var tx = db.BeginTransaction())
        {
            tx.SetVector(EntityKind.Vertex, vertex.Value, IndexName, [0f, 1f, 0f, 0f]);
            tx.Rollback();
        }

        destination.AsSpan().Clear();
        db.Vectors.TryGetVector(EntityKind.Vertex, vertex.Value, IndexName, destination).Should().BeTrue();
        destination.Should().Equal(1f, 0f, 0f, 0f);
    }

    [Fact]
    public void Hybrid_graph_and_vector_commit_is_atomic_across_reopen()
    {
        long id;
        using (var db = QuiverDatabase.Open(_path))
        {
            db.Vectors.CreateVectorIndex(Spec(db));
            using var tx = db.BeginTransaction();
            var n = tx.CreateVertex("Doc");
            id = n.Value;
            tx.SetVector(EntityKind.Vertex, n.Value, IndexName, new float[] { 1, 0, 0, 0 });
            tx.Commit();
        }

        using (var db = QuiverDatabase.Open(_path))
        {
            // Vertexもベクトルも一緒に永続化されている。
            using var rtx = db.BeginReadOnlyTransaction();
            rtx.VertexExists(new VertexId(id)).Should().BeTrue();

            Knn(db, new float[] { 1, 0, 0, 0 }, 10).Should().ContainSingle()
                .Which.Should().Be(EntityRef.UnpackSequence(id));
        }
    }

    [Fact]
    public void Autocommit_SetVector_persists_without_explicit_tx()
    {
        long seq;
        using (var db = QuiverDatabase.Open(_path))
        {
            db.Vectors.CreateVectorIndex(Spec(db));
            // tx を一切張らずに直接 SetVector → autocommit で crash-atomic に永続化される。
            VertexId n;
            using (var tx = db.BeginTransaction()) { n = tx.CreateVertex("Doc"); tx.Commit(); }
            seq = EntityRef.UnpackSequence(n.Value);
            db.Vectors.SetVector(EntityKind.Vertex, n.Value, IndexName, new float[] { 0, 1, 0, 0 });
        }

        using (var db = QuiverDatabase.Open(_path))
        {
            Knn(db, new float[] { 0, 1, 0, 0 }, 10).Should().ContainSingle()
                .Which.Should().Be(seq);
        }
    }

    [Fact]
    public void SetVector_in_readonly_tx_throws()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(Spec(db));
        using var rtx = db.BeginReadOnlyTransaction();
        var act = () => rtx.SetVector(EntityKind.Vertex, 0, IndexName, new float[] { 1, 0, 0, 0 });
        act.Should().Throw<TransactionException>();
    }
}
