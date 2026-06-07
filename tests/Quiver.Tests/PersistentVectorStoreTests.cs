using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ARCH-6 (6a): ベクトル payload / index catalog が単一ファイルに永続化され、close → reopen を
/// 跨いで KNN 検索が再現することを検証する。SetVector を write tx の内側で呼ぶと、その page 書き込みは
/// container の WAL に乗ってグラフ変更と一緒に commit される (ambient WalPageContext)。
/// </summary>
public sealed class PersistentVectorStoreTests : IDisposable
{
    private const string IndexName = "embed";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly string _path;

    public PersistentVectorStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_arch6_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Vectors_survive_close_and_reopen()
    {
        var ids = new long[3];

        // セッション 1: index を作り、3 件のベクトルを write tx 内で set してコミット → close。
        using (var db = GraphDatabase.Open(_path))
        {
            var keyId = db.Schema.GetOrCreatePropertyKey("title");
            db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                IndexName, EntityKind.Node, keyId, Dim, DistanceMetric.Cosine, "test", null));

            using var tx = db.BeginTransaction();
            for (int i = 0; i < 3; i++)
            {
                var nid = tx.CreateNode("Doc");
                ids[i] = nid.Value;
                var vec = new float[Dim];
                vec[i] = 1f;
                db.Vectors.SetVector(EntityKind.Node, nid.Value, IndexName, vec);
            }
            tx.Commit();
        }

        // セッション 2: 同じファイルを開き直す。index spec と payload が復元されること。
        using (var db = GraphDatabase.Open(_path))
        {
            db.Vectors.TryGetIndex(IndexName, out var spec).Should().BeTrue();
            spec.Dimensions.Should().Be(Dim);
            spec.Metric.Should().Be(DistanceMetric.Cosine);

            // ids[1] に平行なクエリ → ids[1] が先頭。
            var query = new float[] { 0f, 1f, 0f, 0f };
            using var cursor = db.Vectors.KnnSearch(IndexName, query, k: 2);

            var got = new List<long>();
            while (cursor.MoveNext()) got.Add(cursor.Current.EntityId);

            got.Should().HaveCount(2);
            got[0].Should().Be(EntityRef.Sequence(ids[1]));
        }
    }

    [Fact]
    public void RemoveVector_persists_across_reopen()
    {
        long keep, drop;
        using (var db = GraphDatabase.Open(_path))
        {
            var keyId = db.Schema.GetOrCreatePropertyKey("title");
            db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                IndexName, EntityKind.Node, keyId, Dim, DistanceMetric.Dot, "test", null));

            using var tx = db.BeginTransaction();
            var a = tx.CreateNode("Doc");
            var b = tx.CreateNode("Doc");
            keep = a.Value; drop = b.Value;
            db.Vectors.SetVector(EntityKind.Node, a.Value, IndexName, new float[] { 1, 0, 0, 0 });
            db.Vectors.SetVector(EntityKind.Node, b.Value, IndexName, new float[] { 1, 0, 0, 0 });
            db.Vectors.RemoveVector(EntityKind.Node, b.Value, IndexName);
            tx.Commit();
        }

        using (var db = GraphDatabase.Open(_path))
        {
            using var cursor = db.Vectors.KnnSearch(IndexName, new float[] { 1, 0, 0, 0 }, k: 10);
            var got = new List<long>();
            while (cursor.MoveNext()) got.Add(cursor.Current.EntityId);

            got.Should().ContainSingle();
            got[0].Should().Be(EntityRef.Sequence(keep));
            got.Should().NotContain(EntityRef.Sequence(drop));
        }
    }
}
