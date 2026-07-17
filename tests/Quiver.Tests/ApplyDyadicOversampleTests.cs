using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// HNSW で候補を多めに取得してからカスタム演算子で再順位付けする処理を検証する。
/// <c>oversample</c> が HNSW の候補数を制御し、その候補だけが再評価されることを確認する。
/// </summary>
public sealed class ApplyDyadicOversampleTests : IDisposable
{
    private const string VecIndex = "Waveform";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public ApplyDyadicOversampleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sig7_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.Schema.GetOrCreatePropertyKey(VecIndex);
        // 既定の HnswFlat は KNN と ApplyDyadic の両方を扱う。
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VecIndex, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "test", null, VectorIndexKind.HnswFlat));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Oversample_rerank_returns_correct_top_k()
    {
        // 異なるベクトルを持つVertexを 20 件作る。
        float[] query = [1f, 0f, 0f, 0f];
        var created = new List<(VertexId Id, float[] Vec)>();

        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 20; i++)
            {
                var nid = tx.CreateVertex("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                // クエリとの一致度が順に下がるベクトル。
                var vec = new float[Dim];
                vec[0] = 1f - i * 0.05f;
                vec[1] = i * 0.05f;
                Normalize(vec);
                tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex, vec);
                created.Add((nid, vec));
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // 候補追加取得を使わない全走査を正解値にする。
        var bruteResult = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 5)
            .ToListWithIds();

        // HNSW が 5×4=20 件を候補に絞り、その後に再順位付けする。
        var oversampleResult = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 5, oversample: 4)
            .ToListWithIds();

        // Vertexが 20 件だけなので oversample=4 では全件が候補となり、結果が一致する。
        oversampleResult.Should().HaveCount(bruteResult.Count);
        oversampleResult.Select(x => x.Id).Should().Equal(bruteResult.Select(x => x.Id));
    }

    [Fact]
    public void Oversample_narrows_candidates_from_hnsw()
    {
        // 30 Vertexを作る。k=3、oversample=2 なら HNSW は 6 候補を返す。
        float[] query = [1f, 0f, 0f, 0f];

        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 30; i++)
            {
                var nid = tx.CreateVertex("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                var vec = new float[Dim];
                vec[0] = 1f - i * 0.03f;
                vec[1] = i * 0.03f;
                Normalize(vec);
                tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex, vec);
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // 6 候補を再順位付けし、上位 3 件を返す。
        var result = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 3, oversample: 2)
            .ToListWithIds();

        result.Should().HaveCount(3);
    }

    [Fact]
    public void Oversample_with_flatonly_throws()
    {
        var flatDir = Path.Combine(Path.GetTempPath(), "quiver_sig7_flat_" + Guid.NewGuid().ToString("N"));
        var flatDb = QuiverDatabase.Open(Path.Combine(flatDir, "graph.quiver"));
        try
        {
            var keyId = flatDb.Schema.GetOrCreatePropertyKey(VecIndex);
            flatDb.Vectors.CreateVectorIndex(new VectorIndexSpec(
                VecIndex, EntityKind.Vertex, keyId, Dim,
                DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly));

            using (var tx = flatDb.BeginTransaction())
            {
                var nid = tx.CreateVertex("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex, [1f, 0f, 0f, 0f]);
                tx.Commit();
            }

            using var rtx = flatDb.BeginReadOnlyTransaction();
            var g = rtx.G(flatDb.Schema);

            var act = () => g.Vertices<SensorVertex>().Has(s => s.Site, "A")
                .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 1, oversample: 4)
                .ToList();

            act.Should().Throw<VectorException>().WithMessage("*FlatOnly*");
        }
        finally
        {
            flatDb.Dispose();
            if (Directory.Exists(flatDir)) Directory.Delete(flatDir, recursive: true);
        }
    }

    [Fact]
    public void Oversample_zero_or_negative_throws()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var act0 = () => g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 1, oversample: 0);
        act0.Should().Throw<ArgumentOutOfRangeException>();

        var actNeg = () => g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 1, oversample: -1);
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Oversample_with_traversal_b()
    {
        float[] refVec = [0f, 1f, 0f, 0f];

        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var nid = tx.CreateVertex("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                var vec = new float[Dim];
                vec[i % Dim] = 1f;
                tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex, vec);
            }

            var tmpl = tx.CreateVertex("Template");
            tx.SetProperty(tmpl, "Name", PropertyValue.FromString("ref"));
            tx.SetProperty(tmpl, "Pattern", PropertyValue.FromFloatArray(refVec));
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var traversalB = g.Vertices<TemplateVertex>()
            .Has(t => t.Name, "ref")
            .Values(t => t.Pattern);

        var result = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, traversalB, k: 3, oversample: 4)
            .ToListWithIds();

        result.Should().HaveCountGreaterThan(0);
        result.Should().HaveCountLessOrEqualTo(3);
    }

    [Fact]
    public void Oversample_null_falls_back_to_brute()
    {
        float[] query = [1f, 0f, 0f, 0f];

        using (var tx = _db.BeginTransaction())
        {
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

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // oversample が null の既定値は候補追加取得なしと同じ動作になる。
        var bruteResult = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 3)
            .ToListWithIds();

        var explicitNull = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 3, oversample: null)
            .ToListWithIds();

        explicitNull.Select(x => x.Id).Should().Equal(bruteResult.Select(x => x.Id));
    }

    [Fact]
    public void Oversample_empty_upstream_returns_empty()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // ラベル Sensor かつ Site="Z" に一致するVertexはない。
        var result = g.Vertices<SensorVertex>().Has(s => s.Site, "Z")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 5, oversample: 4)
            .ToListWithIds();

        result.Should().BeEmpty();
    }

    private static void Normalize(float[] v)
    {
        float norm = 0f;
        foreach (var x in v) norm += x * x;
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    // ── 手書きのモデル型 ──────────────────────────────────────────────────

    private sealed class SensorVertex : IGraphVertex<SensorVertex>
    {
        public string Site { get; set; } = "";
        public float[] Waveform { get; set; } = [];

        public static string GraphLabel => "Sensor";

        public static VertexId Insert(IGraphTransaction tx, SensorVertex entity)
        {
            var id = tx.CreateVertex(GraphLabel);
            tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
            return id;
        }

        public static VertexId InsertIndexed(IGraphTransaction tx, SensorVertex entity) => Insert(tx, entity);

        public static SensorVertex Load(IGraphTransaction tx, VertexId id)
            => new()
            {
                Site = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Site").Utf8StringValue),
            };

        public static void Update(IGraphTransaction tx, VertexId id, SensorVertex entity)
            => tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));

        public static void Delete(IGraphTransaction tx, VertexId id)
            => tx.DeleteVertex(id);
    }

    private sealed class TemplateVertex : IGraphVertex<TemplateVertex>
    {
        public string Name { get; set; } = "";
        public float[] Pattern { get; set; } = [];

        public static string GraphLabel => "Template";

        public static VertexId Insert(IGraphTransaction tx, TemplateVertex entity)
        {
            var id = tx.CreateVertex(GraphLabel);
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
            return id;
        }

        public static VertexId InsertIndexed(IGraphTransaction tx, TemplateVertex entity) => Insert(tx, entity);

        public static TemplateVertex Load(IGraphTransaction tx, VertexId id)
        {
            var nameVal = tx.GetProperty(id, "Name");
            var patternVal = tx.GetProperty(id, "Pattern");
            return new()
            {
                Name = System.Text.Encoding.UTF8.GetString(nameVal.Utf8StringValue),
                Pattern = patternVal.Type == PropertyValueType.FloatArray
                    ? patternVal.FloatArrayValue.ToArray()
                    : [],
            };
        }

        public static void Update(IGraphTransaction tx, VertexId id, TemplateVertex entity)
        {
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
        }

        public static void Delete(IGraphTransaction tx, VertexId id)
            => tx.DeleteVertex(id);
    }
}
