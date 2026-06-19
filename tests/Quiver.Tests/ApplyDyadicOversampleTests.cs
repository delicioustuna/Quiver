using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// SIG-7: HNSW oversample → custom rerank. Tests that the <c>oversample</c>
/// parameter narrows candidates via HNSW before re-ranking with the custom operator.
/// </summary>
public sealed class ApplyDyadicOversampleTests : IDisposable
{
    private const string VecIndex = "Waveform";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public ApplyDyadicOversampleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sig7_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.Schema.GetOrCreatePropertyKey(VecIndex);
        // HnswFlat (default) — supports both KNN and ApplyDyadic
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VecIndex, EntityKind.Node, keyId, Dim,
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
        // Create 20 nodes with distinct vectors
        float[] query = [1f, 0f, 0f, 0f];
        var created = new List<(NodeId Id, float[] Vec)>();

        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 20; i++)
            {
                var nid = tx.CreateNode("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                // Vectors with decreasing alignment to query
                var vec = new float[Dim];
                vec[0] = 1f - i * 0.05f;
                vec[1] = i * 0.05f;
                Normalize(vec);
                _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, vec);
                created.Add((nid, vec));
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // Brute-force (no oversample) — exact result
        var bruteResult = g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 5)
            .ToListWithIds();

        // Oversample — HNSW pre-filters 5*4=20 candidates (all), then reranks
        var oversampleResult = g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 5, oversample: 4)
            .ToListWithIds();

        // With oversample=4 and only 20 nodes, HNSW gets all of them anyway, so results match
        oversampleResult.Should().HaveCount(bruteResult.Count);
        oversampleResult.Select(x => x.Id).Should().Equal(bruteResult.Select(x => x.Id));
    }

    [Fact]
    public void Oversample_narrows_candidates_from_hnsw()
    {
        // Create 30 nodes — the oversample=2 with k=3 means HNSW returns 6 candidates
        float[] query = [1f, 0f, 0f, 0f];

        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 30; i++)
            {
                var nid = tx.CreateNode("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                var vec = new float[Dim];
                vec[0] = 1f - i * 0.03f;
                vec[1] = i * 0.03f;
                Normalize(vec);
                _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, vec);
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // k=3, oversample=2 → HNSW returns 6 candidates → rerank → top 3
        var result = g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 3, oversample: 2)
            .ToListWithIds();

        result.Should().HaveCount(3);
    }

    [Fact]
    public void Oversample_with_flatonly_throws()
    {
        var flatDir = Path.Combine(Path.GetTempPath(), "quiver_sig7_flat_" + Guid.NewGuid().ToString("N"));
        var flatDb = GraphDatabase.Open(Path.Combine(flatDir, "graph.quiver"));
        try
        {
            var keyId = flatDb.Schema.GetOrCreatePropertyKey(VecIndex);
            flatDb.Vectors.CreateVectorIndex(new VectorIndexSpec(
                VecIndex, EntityKind.Node, keyId, Dim,
                DistanceMetric.Cosine, "test", null, VectorIndexKind.FlatOnly));

            using (var tx = flatDb.BeginTransaction())
            {
                var nid = tx.CreateNode("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                flatDb.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, [1f, 0f, 0f, 0f]);
                tx.Commit();
            }

            using var rtx = flatDb.BeginReadOnlyTransaction();
            var g = rtx.G(flatDb.Schema);

            var act = () => g.Nodes<SensorNode>().Has(s => s.Site, "A")
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

        var act0 = () => g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, [1f, 0f, 0f, 0f], k: 1, oversample: 0);
        act0.Should().Throw<ArgumentOutOfRangeException>();

        var actNeg = () => g.Nodes<SensorNode>().Has(s => s.Site, "A")
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
                var nid = tx.CreateNode("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                var vec = new float[Dim];
                vec[i % Dim] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, vec);
            }

            var tmpl = tx.CreateNode("Template");
            tx.SetProperty(tmpl, "Name", PropertyValue.FromString("ref"));
            tx.SetProperty(tmpl, "Pattern", PropertyValue.FromFloatArray(refVec));
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        var traversalB = g.Nodes<TemplateNode>()
            .Has(t => t.Name, "ref")
            .Values(t => t.Pattern);

        var result = g.Nodes<SensorNode>().Has(s => s.Site, "A")
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
                var nid = tx.CreateNode("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                var vec = new float[Dim];
                vec[i % Dim] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, vec);
            }
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // oversample: null (default) — should behave identically to no oversample
        var bruteResult = g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 3)
            .ToListWithIds();

        var explicitNull = g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 3, oversample: null)
            .ToListWithIds();

        explicitNull.Select(x => x.Id).Should().Equal(bruteResult.Select(x => x.Id));
    }

    [Fact]
    public void Oversample_empty_upstream_returns_empty()
    {
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // No matching nodes for label "Sensor" with Site="Z"
        var result = g.Nodes<SensorNode>().Has(s => s.Site, "Z")
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

    // ── Hand-coded model types ──────────────────────────────────────────────

    private sealed class SensorNode : IGraphNode<SensorNode>
    {
        public string Site { get; set; } = "";
        public float[] Waveform { get; set; } = [];

        public static string GraphLabel => "Sensor";

        public static NodeId Insert(IGraphTransaction tx, SensorNode entity)
        {
            var id = tx.CreateNode(GraphLabel);
            tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
            return id;
        }

        public static NodeId InsertIndexed(IGraphTransaction tx, SensorNode entity) => Insert(tx, entity);

        public static SensorNode Load(IGraphTransaction tx, NodeId id)
            => new()
            {
                Site = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Site").Utf8StringValue),
            };

        public static void Update(IGraphTransaction tx, NodeId id, SensorNode entity)
            => tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));

        public static void Delete(IGraphTransaction tx, NodeId id)
            => tx.DeleteNode(id);
    }

    private sealed class TemplateNode : IGraphNode<TemplateNode>
    {
        public string Name { get; set; } = "";
        public float[] Pattern { get; set; } = [];

        public static string GraphLabel => "Template";

        public static NodeId Insert(IGraphTransaction tx, TemplateNode entity)
        {
            var id = tx.CreateNode(GraphLabel);
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
            return id;
        }

        public static NodeId InsertIndexed(IGraphTransaction tx, TemplateNode entity) => Insert(tx, entity);

        public static TemplateNode Load(IGraphTransaction tx, NodeId id)
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

        public static void Update(IGraphTransaction tx, NodeId id, TemplateNode entity)
        {
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
        }

        public static void Delete(IGraphTransaction tx, NodeId id)
            => tx.DeleteNode(id);
    }
}
