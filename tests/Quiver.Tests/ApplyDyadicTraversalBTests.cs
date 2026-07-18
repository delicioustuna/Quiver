using System.Runtime.InteropServices;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <c>GraphTraversal&lt;float[]&gt;</c> を右オペランドに指定した
/// <c>ApplyDyadic</c> を検証する。
/// 非相関のトラバーサルはオープン時に一度だけ評価され、
/// その結果が全候補を採点する参照ベクトルになる。
/// </summary>
public sealed class ApplyDyadicTraversalBTests : IDisposable
{
    private const string VecIndex = "Waveform";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public ApplyDyadicTraversalBTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sig5_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.EditSchema(schema => schema.GetOrCreatePropertyKey(VecIndex));
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VecIndex, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "test", null));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ApplyDyadic_traversal_b_matches_static_b()
    {
        float[] refVec = [0f, 1f, 0f, 0f];

        // 異なる one-hot ベクトルを持つ Sensor Vertexを 3 件作る。
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                var nid = tx.CreateVertex("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                var vec = new float[Dim];
                vec[i] = 1f;
                tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex, vec);
            }

            // 参照ベクトルを float[] プロパティに持つ Template Vertexを作る。
            var tmpl = tx.CreateVertex("Template");
            tx.SetProperty(tmpl, "Name", PropertyValue.FromString("ref"));
            tx.SetProperty(tmpl, "Pattern", PropertyValue.FromFloatArray(refVec));

            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // 固定値 b を基準結果にする。
        var staticResult = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, refVec, k: 3)
            .ToListWithIds();

        // トラバーサル b は Template の Pattern を float[] として読む。
        var traversalB = g.Vertices<TemplateVertex>()
            .Has(t => t.Name, "ref")
            .Values(t => t.Pattern);

        var traversalResult = g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, traversalB, k: 3)
            .ToListWithIds();

        // 両経路の順位が一致することを確認する。
        traversalResult.Should().HaveCount(staticResult.Count);
        var staticIds = staticResult.Select(x => x.Id).ToList();
        var traversalIds = traversalResult.Select(x => x.Id).ToList();
        traversalIds.Should().Equal(staticIds);
    }

    [Fact]
    public void ApplyDyadic_traversal_b_empty_throws()
    {
        // 入力を空にしないため Sensor Vertexを作る。
        using (var tx = _db.BeginWriteTransaction())
        {
            var nid = tx.CreateVertex("Sensor");
            tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
            tx.SetVector(EntityKind.Vertex, nid.Value, VecIndex, [1f, 0f, 0f, 0f]);
            tx.Commit();
        }

        using var rtx = _db.BeginReadTransaction();
        var g = rtx.Query;

        // 何にも一致しないトラバーサル b。
        var emptyB = g.Vertices<TemplateVertex>()
            .Has(t => t.Name, "does_not_exist")
            .Values(t => t.Pattern);

        var act = () => g.Vertices<SensorVertex>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, emptyB, k: 1)
            .ToList();

        act.Should().Throw<VectorException>()
            .WithMessage("*no results*");
    }

    // ── 手書きのモデル型 ────────────────────────────────────────────────

    private sealed class SensorVertex : IGraphVertex<SensorVertex>
    {
        public string Site { get; set; } = "";
        public float[] Waveform { get; set; } = [];

        public static string GraphLabel => "Sensor";

        public static VertexId Insert(IWriteTransaction tx, SensorVertex entity)
        {
            var id = tx.CreateVertex(GraphLabel);
            tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
            return id;
        }

        public static VertexId InsertIndexed(IWriteTransaction tx, SensorVertex entity) => Insert(tx, entity);

        public static SensorVertex Load(IReadTransaction tx, VertexId id)
            => new()
            {
                Site = System.Text.Encoding.UTF8.GetString(tx.GetProperty(id, "Site").Utf8StringValue),
            };

        public static void Update(IWriteTransaction tx, VertexId id, SensorVertex entity)
            => tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));

        public static void Delete(IWriteTransaction tx, VertexId id)
            => tx.DeleteVertex(id);
    }

    private sealed class TemplateVertex : IGraphVertex<TemplateVertex>
    {
        public string Name { get; set; } = "";
        public float[] Pattern { get; set; } = [];

        public static string GraphLabel => "Template";

        public static VertexId Insert(IWriteTransaction tx, TemplateVertex entity)
        {
            var id = tx.CreateVertex(GraphLabel);
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
            return id;
        }

        public static VertexId InsertIndexed(IWriteTransaction tx, TemplateVertex entity) => Insert(tx, entity);

        public static TemplateVertex Load(IReadTransaction tx, VertexId id)
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

        public static void Update(IWriteTransaction tx, VertexId id, TemplateVertex entity)
        {
            tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
            tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
        }

        public static void Delete(IWriteTransaction tx, VertexId id)
            => tx.DeleteVertex(id);
    }
}
