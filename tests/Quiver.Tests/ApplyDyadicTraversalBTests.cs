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
    private readonly GraphDatabase _db;

    public ApplyDyadicTraversalBTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sig5_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        var keyId = _db.Schema.GetOrCreatePropertyKey(VecIndex);
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            VecIndex, EntityKind.Node, keyId, Dim,
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

        // 異なる one-hot ベクトルを持つ Sensor ノードを 3 件作る。
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                var nid = tx.CreateNode("Sensor");
                tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
                var vec = new float[Dim];
                vec[i] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, vec);
            }

            // 参照ベクトルを float[] プロパティに持つ Template ノードを作る。
            var tmpl = tx.CreateNode("Template");
            tx.SetProperty(tmpl, "Name", PropertyValue.FromString("ref"));
            tx.SetProperty(tmpl, "Pattern", PropertyValue.FromFloatArray(refVec));

            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // 固定値 b を基準結果にする。
        var staticResult = g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, refVec, k: 3)
            .ToListWithIds();

        // トラバーサル b は Template の Pattern を float[] として読む。
        var traversalB = g.Nodes<TemplateNode>()
            .Has(t => t.Name, "ref")
            .Values(t => t.Pattern);

        var traversalResult = g.Nodes<SensorNode>().Has(s => s.Site, "A")
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
        // 入力を空にしないため Sensor ノードを作る。
        using (var tx = _db.BeginTransaction())
        {
            var nid = tx.CreateNode("Sensor");
            tx.SetProperty(nid, "Site", PropertyValue.FromString("A"));
            _db.Vectors.SetVector(EntityKind.Node, nid.Value, VecIndex, [1f, 0f, 0f, 0f]);
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema);

        // 何にも一致しないトラバーサル b。
        var emptyB = g.Nodes<TemplateNode>()
            .Has(t => t.Name, "does_not_exist")
            .Values(t => t.Pattern);

        var act = () => g.Nodes<SensorNode>().Has(s => s.Site, "A")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, emptyB, k: 1)
            .ToList();

        act.Should().Throw<VectorException>()
            .WithMessage("*no results*");
    }

    // ── 手書きのモデル型 ────────────────────────────────────────────────

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
