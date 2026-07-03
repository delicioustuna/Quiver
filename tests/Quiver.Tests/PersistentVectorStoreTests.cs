using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ベクトルペイロードとインデックスカタログが単一ファイルに永続化され、
/// 再オープン後も KNN 検索を再現できることを検証する。
/// 書き込みトランザクション内の <c>SetVector</c> によるページ変更は
/// コンテナの WAL に記録され、グラフ変更と同時にコミットされる。
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
    public void Catalog_round_trips_multiple_entries_and_custom_hnsw_layout()
    {
        const string secondIndex = "embed-wide";
        long id;
        using (var db = GraphDatabase.Open(_path))
        {
            var keyId = db.Schema.GetOrCreatePropertyKey("title");
            db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                IndexName,
                EntityKind.Node,
                keyId,
                Dim,
                DistanceMetric.Cosine,
                "test",
                HnswM: 4,
                HnswMMax0: 6,
                HnswMaxLayers: 3,
                HnswEfConstruction: 24));
            db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                secondIndex,
                EntityKind.Node,
                keyId,
                Dim,
                DistanceMetric.Dot,
                "test-2",
                HnswM: 8,
                HnswMMax0: 12,
                HnswMaxLayers: 5,
                HnswEfConstruction: 40));

            using var tx = db.BeginTransaction();
            var node = tx.CreateNode("Doc");
            id = node.Value;
            db.Vectors.SetVector(
                EntityKind.Node,
                node.Value,
                IndexName,
                [1f, 0f, 0f, 0f]);
            tx.Commit();
        }

        using (var db = GraphDatabase.Open(_path))
        {
            db.Vectors.TryGetIndex(IndexName, out var first).Should().BeTrue();
            first.HnswM.Should().Be(4);
            first.HnswMMax0.Should().Be(6);
            first.HnswMaxLayers.Should().Be(3);
            first.HnswEfConstruction.Should().Be(24);

            db.Vectors.TryGetIndex(secondIndex, out var second).Should().BeTrue();
            second.HnswM.Should().Be(8);
            second.HnswMMax0.Should().Be(12);
            second.HnswMaxLayers.Should().Be(5);
            second.HnswEfConstruction.Should().Be(40);

            using var cursor = db.Vectors.KnnSearch(IndexName, [1f, 0f, 0f, 0f], 1);
            cursor.MoveNext().Should().BeTrue();
            cursor.Current.EntityId.Should().Be(EntityRef.Sequence(id));
        }
    }

    [Fact]
    public void ElementType_defaults_to_float32_and_round_trips()
    {
        using (var db = GraphDatabase.Open(_path))
        {
            var keyId = db.Schema.GetOrCreatePropertyKey("title");
            db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                IndexName, EntityKind.Node, keyId, Dim, DistanceMetric.Cosine, "test"));
        }

        using (var db = GraphDatabase.Open(_path))
        {
            db.Vectors.TryGetIndex(IndexName, out var spec).Should().BeTrue();
            spec.ElementType.Should().Be(VectorElementType.Float32);
        }
    }

    [Fact]
    public void Unsupported_element_type_is_rejected_at_creation()
    {
        using var db = GraphDatabase.Open(_path);
        var keyId = db.Schema.GetOrCreatePropertyKey("title");
        var act = () => db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, keyId, Dim, DistanceMetric.Cosine, "test",
            ElementType: (VectorElementType)7));

        act.Should().Throw<VectorException>()
            .WithMessage("*unsupported element type*");
    }

    [Fact]
    public void V1_vector_catalog_is_rejected_as_a_clean_break()
    {
        using var file = new InMemoryPagedFile();
        _ = new VectorIndexCatalog(file);
        var header = file.PinForWrite(new PageId(1));
        header.Data[31] = FormatVersion.V1;
        file.UnpinDirty(new PageId(1), 0);

        var reopen = () => new VectorIndexCatalog(file);
        reopen.Should().Throw<FormatVersionMismatchException>()
            .Which.Should().Match<FormatVersionMismatchException>(
                ex => ex.FileKind == "vectorcatalog"
                    && ex.Found == FormatVersion.V1
                    && ex.Expected == FormatVersion.V2);
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

    [Fact]
    public void FlatOnly_SetVector_and_TryGetVector_survive_reopen()
    {
        long nid;
        using (var db = GraphDatabase.Open(_path))
        {
            var keyId = db.Schema.GetOrCreatePropertyKey("waveform");
            db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                IndexName, EntityKind.Node, keyId, Dim, DistanceMetric.Cosine, "test", null,
                VectorIndexKind.FlatOnly));

            using var tx = db.BeginTransaction();
            var n = tx.CreateNode("Sensor");
            nid = n.Value;
            db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, [1f, 2f, 3f, 4f]);
            tx.Commit();
        }

        using (var db = GraphDatabase.Open(_path))
        {
            db.Vectors.TryGetIndex(IndexName, out var spec).Should().BeTrue();
            spec.IndexKind.Should().Be(VectorIndexKind.FlatOnly);

            var buf = new float[Dim];
            db.Vectors.TryGetVector(EntityKind.Node, EntityRef.Sequence(nid), IndexName, buf).Should().BeTrue();
            buf.Should().Equal(1f, 2f, 3f, 4f);
        }
    }

    [Fact]
    public void FlatOnly_KnnSearch_throws()
    {
        using var db = GraphDatabase.Open(_path);
        var keyId = db.Schema.GetOrCreatePropertyKey("waveform");
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, keyId, Dim, DistanceMetric.Cosine, "test", null,
            VectorIndexKind.FlatOnly));

        var act = () => db.Vectors.KnnSearch(IndexName, new float[Dim], 1);
        act.Should().Throw<VectorException>().WithMessage("*FlatOnly*");
    }

    [Fact]
    public void FlatOnly_KnnSearchBatch_throws()
    {
        using var db = GraphDatabase.Open(_path);
        var keyId = db.Schema.GetOrCreatePropertyKey("waveform");
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, keyId, Dim, DistanceMetric.Cosine, "test", null,
            VectorIndexKind.FlatOnly));

        var queries = new ReadOnlyMemory<float>[] { new float[Dim] };
        var act = () => db.Vectors.KnnSearchBatch(IndexName, queries, 1);
        act.Should().Throw<VectorException>().WithMessage("*FlatOnly*");
    }
}
