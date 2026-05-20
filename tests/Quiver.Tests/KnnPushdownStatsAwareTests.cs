using FluentAssertions;
using Quiver.Client;
using Quiver.Client.Internal;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// VEC-10 coverage: statistics-aware fallback in <see cref="PendingKnnBuilder.Materialize(GraphStats?, Quiver.ISchemaApi?)"/>.
/// 構造ヒントが graph-first を示唆していても label cardinality / TotalNodes が
/// <see cref="PendingKnnBuilder.VectorFirstLabelFraction"/> (既定 30%) 以上のときは vector-first にフォールバックする。
/// stats を渡さない場合 (g.G(schema) 経由) は VEC-9 と同じ構造ヒントのみで graph-first を選ぶ。
/// </summary>
public sealed class KnnPushdownStatsAwareTests : IDisposable
{
    private const string IndexName = "doc-embed";
    private const int Dim = 4;
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public KnnPushdownStatsAwareTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_vec10_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(_dir);

        var keyId = _db.Schema.GetOrCreatePropertyKey("title");
        _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, keyId, Dim,
            DistanceMetric.Cosine, "test", null));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// 50 Doc + 50 Other (Doc cardinality = 50%) で <c>Knn().HasLabel("Doc")</c> をリライトすると、
    /// stats を注入したケースで vector-first にフォールバックする。
    /// VEC-11 後 backend は <see cref="GraphStats.HasFastLabelIndex"/> = <c>true</c> を立てるため、
    /// dim = 4 (テスト用小次元) の dim-aware piecewise table の最小バケット閾値 0.30 を使う。
    /// 50% &gt;= 0.30 で fallback 発火、<see cref="KnnNodeSourceBuilder"/> + 後段 <see cref="FilterBuilder"/> になる。
    /// </summary>
    [Fact]
    public void HighLabelCardinality_falls_back_to_vector_first_when_stats_present()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                var d = tx.CreateNode("Doc");
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            for (int i = 0; i < 50; i++)
            {
                var o = tx.CreateNode("Other");
                _db.Vectors.SetVector(EntityKind.Node, o.Value, IndexName, new float[] { 0, 1, 0, 0 });
            }
            tx.Commit();
        }

        var stats = _db.CollectStats();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema, stats);
        var traversal = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 5).HasLabel("Doc");

        var pk = ReadPending(traversal);
        var materialized = pk.Materialize(stats, _db.Schema);
        materialized.Should().BeOfType<FilterBuilder>("label cardinality 50% >= 30% threshold → vector-first fallback");
    }

    /// <summary>
    /// 同じ構造でも stats を渡さない (g.G(schema)) と VEC-9 の構造ヒントのみで判定するため
    /// <see cref="FilteredKnnNodeSourceBuilder"/> (graph-first) を選ぶ。後方互換性の確認。
    /// </summary>
    [Fact]
    public void HighLabelCardinality_keeps_graph_first_when_stats_absent()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 50; i++)
            {
                var d = tx.CreateNode("Doc");
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            for (int i = 0; i < 50; i++)
                _ = tx.CreateNode("Other");
            tx.Commit();
        }

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema); // stats 注入なし
        var traversal = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 5).HasLabel("Doc");

        var pk = ReadPending(traversal);
        pk.Materialize().Should().BeOfType<FilteredKnnNodeSourceBuilder>("stats 不在 → 構造ヒントのみで graph-first");
    }

    /// <summary>
    /// 5 Doc + 95 Other (Doc cardinality = 5%) では label cardinality が 30% を下回るため、
    /// stats を渡しても graph-first を維持する (VEC-9 と同じ振る舞い)。
    /// </summary>
    [Fact]
    public void LowLabelCardinality_keeps_graph_first_with_stats()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var d = tx.CreateNode("Doc");
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            for (int i = 0; i < 95; i++)
            {
                var o = tx.CreateNode("Other");
                _db.Vectors.SetVector(EntityKind.Node, o.Value, IndexName, new float[] { 0, 1, 0, 0 });
            }
            tx.Commit();
        }

        var stats = _db.CollectStats();

        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema, stats);
        var traversal = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 5).HasLabel("Doc");

        var pk = ReadPending(traversal);
        pk.Materialize(stats, _db.Schema).Should().BeOfType<FilteredKnnNodeSourceBuilder>(
            "label cardinality 5% < 30% → graph-first 維持");
    }

    /// <summary>
    /// vector-first フォールバック経路でも最終結果は graph-first と同一であること
    /// (filter chain の再配置が正しく行われている)。50/50 で stats 注入有 vs 無 を比較。
    /// </summary>
    [Fact]
    public void Vector_first_fallback_produces_same_results_as_graph_first()
    {
        long[] docIds;
        using (var tx = _db.BeginTransaction())
        {
            docIds = new long[50];
            for (int i = 0; i < 50; i++)
            {
                var d = tx.CreateNode("Doc");
                docIds[i] = d.Value;
                var v = new float[Dim];
                v[i % Dim] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, v);
            }
            for (int i = 0; i < 50; i++)
            {
                var o = tx.CreateNode("Other");
                var v = new float[Dim];
                v[i % Dim] = 1f;
                _db.Vectors.SetVector(EntityKind.Node, o.Value, IndexName, v);
            }
            tx.Commit();
        }

        var stats = _db.CollectStats();
        var query = new float[] { 1f, 0f, 0f, 0f };

        using var rtx = _db.BeginReadOnlyTransaction();

        var withStats = rtx.G(_db.Schema, stats)
            .Knn(IndexName, query, k: 5).HasLabel("Doc").ToList();
        var withoutStats = rtx.G(_db.Schema)
            .Knn(IndexName, query, k: 5).HasLabel("Doc").ToList();

        // 結果集合 (set 等価)。順序はスコアタイブレーカ次第なのでセット比較で十分。
        withStats.Select(n => n.Value).Should().BeEquivalentTo(withoutStats.Select(n => n.Value));
        withStats.Should().OnlyContain(n => docIds.Contains(n.Value));
    }

    /// <summary>
    /// vector-first フォールバックでも k starvation を起こさないこと。
    /// Doc cardinality を高く (例: 60%) しつつ、k を全 Doc が候補に入る十分大きな値にすれば
    /// post-filter でも結果は揃う。これは VEC-10 の保守的閾値 (30%) が現実的に妥当なことを示す。
    /// </summary>
    [Fact]
    public void Vector_first_fallback_returns_some_docs_when_k_large_enough()
    {
        using (var tx = _db.BeginTransaction())
        {
            // 6 Doc + 4 Other, 全員が query 方向に揃ったベクトルを持つ。
            // k=10 なら全件が KNN 結果に入り、後段 HasLabel("Doc") で 6 件残る。
            for (int i = 0; i < 6; i++)
            {
                var d = tx.CreateNode("Doc");
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            for (int i = 0; i < 4; i++)
            {
                var o = tx.CreateNode("Other");
                _db.Vectors.SetVector(EntityKind.Node, o.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            tx.Commit();
        }

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadOnlyTransaction();

        // Doc 60% → fallback 発火。k=10 なら全 10 件が KNN を通り、HasLabel で 6 Doc が残る。
        var result = rtx.G(_db.Schema, stats)
            .Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 10)
            .HasLabel("Doc")
            .ToList();

        result.Should().HaveCount(6);
    }

    /// <summary>
    /// 純粋 property filter (HasLabel なし) で構造ヒントが graph-first を示唆するケース。
    /// candidate チェーンの最内 ScanBuilder に Label が無いため stats 経路では fallback 判定材料が無く、
    /// graph-first を維持する。
    /// </summary>
    [Fact]
    public void Has_only_chain_without_label_keeps_graph_first()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 30; i++)
            {
                var d = tx.CreateNode("Doc");
                tx.SetProperty(d, "status", Stores.PropertyValue.FromString(i == 0 ? "active" : "archived"));
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            tx.Commit();
        }

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema, stats);

        // Has のみ (HasLabel なし) — candidate = FilterBuilder(ScanBuilder(label=null), ...) 形状。
        // label 不在のため FindInnermostScanLabel が null を返し、stats 経路では fallback しない。
        var traversal = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 5).Has("status", "active");
        var pk = ReadPending(traversal);
        pk.Materialize(stats, _db.Schema).Should().BeOfType<FilteredKnnNodeSourceBuilder>(
            "label 抽出不可 → 構造ヒントのみで graph-first 維持");
    }

    /// <summary>
    /// HasLabel + Has (両方) を積んだチェーンで、HasLabel が高 cardinality のとき vector-first へ
    /// フォールバックし、かつ post-filter 経路で Has も維持されること。
    /// </summary>
    [Fact]
    public void HasLabel_plus_Has_pushdown_with_high_cardinality_falls_back_correctly()
    {
        using (var tx = _db.BeginTransaction())
        {
            // 40 Doc + 40 Other (Doc = 50%)。Doc のうち 5 件だけ status=active。
            for (int i = 0; i < 40; i++)
            {
                var d = tx.CreateNode("Doc");
                tx.SetProperty(d, "status", Stores.PropertyValue.FromString(i < 5 ? "active" : "archived"));
                _db.Vectors.SetVector(EntityKind.Node, d.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            for (int i = 0; i < 40; i++)
            {
                var o = tx.CreateNode("Other");
                tx.SetProperty(o, "status", Stores.PropertyValue.FromString("active"));
                _db.Vectors.SetVector(EntityKind.Node, o.Value, IndexName, new float[] { 1, 0, 0, 0 });
            }
            tx.Commit();
        }

        var stats = _db.CollectStats();
        using var rtx = _db.BeginReadOnlyTransaction();
        var g = rtx.G(_db.Schema, stats);

        // k=50 (全件以上) で取り、後段 HasLabel("Doc") + Has("status","active") が両方適用されること。
        var result = g.Knn(IndexName, new float[] { 1, 0, 0, 0 }, k: 50)
            .HasLabel("Doc")
            .Has("status", "active")
            .ToList();

        // Doc かつ status=active は 5 件。
        result.Should().HaveCount(5);
    }

    /// <summary>
    /// VEC-12: dim=768 / sel=40% / HasFastLabelIndex=true のとき、dim-aware piecewise table が
    /// 0.47 を返す (dim ≤ 1024) ため 0.40 &lt; 0.47 で graph-first を維持する。
    /// VEC-11 後の binary backend で「VEC-10 の 0.30 一本だと誤発火する sel=0.40」が
    /// 正しく graph-first に戻ることを示す回帰防止テスト (sweep 実測で dim=768 の
    /// graph-first 12.5ms &lt; vector-first 14.0ms を確認済み)。
    /// </summary>
    [Fact]
    public void Fast_label_index_keeps_graph_first_at_sel_40pct_dim_768()
    {
        const int dim768 = 768;
        // 100 ノード中 Doc が 40 で sel = 40%。ラベル数が大きいとデータセット作成が遅いため
        // テストでは sel をマクロに作って fraction の通り道だけ確認する。
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 40; i++) _ = tx.CreateNode("Doc");
            for (int i = 0; i < 60; i++) _ = tx.CreateNode("Other");
            tx.Commit();
        }

        var stats = _db.CollectStats();
        stats.HasFastLabelIndex.Should().BeTrue("binary backend は LabelNodeIndex sidecar を持つ");

        // dim=768 と分かっているシナリオを再現するため、PendingKnnBuilder を直接構築する。
        // (既存 index は Dim=4 だが threshold lookup は ctor で渡された dim 値だけを見る)
        var candidate = new ScanBuilder("Doc");
        var pk = new PendingKnnBuilder(
            candidate, IndexName, new float[] { 1, 0, 0, 0 }, k: 5, dim: dim768);

        pk.Materialize(stats, _db.Schema)
            .Should().BeOfType<FilteredKnnNodeSourceBuilder>(
                "dim=768 / sel=40% は閾値 0.50 を下回るため graph-first を維持");
    }

    /// <summary>
    /// VEC-12: 同じ dim=768 / sel=40% でも <see cref="GraphStats.HasFastLabelIndex"/> が false の
    /// backend (= sidecar 不在 / ANN bypass / 単体テスト経路) では legacy 30% 単一閾値を引くため
    /// vector-first にフォールバックする。下位互換性確認。
    /// </summary>
    [Fact]
    public void Without_fast_label_index_falls_back_at_sel_40pct()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 40; i++) _ = tx.CreateNode("Doc");
            for (int i = 0; i < 60; i++) _ = tx.CreateNode("Other");
            tx.Commit();
        }

        var stats = _db.CollectStats().WithFastLabelIndex(false);
        stats.HasFastLabelIndex.Should().BeFalse();

        var candidate = new ScanBuilder("Doc");
        var pk = new PendingKnnBuilder(
            candidate, IndexName, new float[] { 1, 0, 0, 0 }, k: 5, dim: 768);

        pk.Materialize(stats, _db.Schema)
            .Should().BeOfType<FilterBuilder>(
                "HasFastLabelIndex=false 経路は legacy 0.30 単一閾値、40% >= 30% で fallback");
    }

    /// <summary>
    /// VEC-12: 同じ sel=45% でも dim=128 と dim=3072 で fallback 発火が分岐すること。
    /// dim=128 → 閾値 0.30 (dim ≤ 512) → 45% &gt;= 0.30 → fallback
    /// dim=3072 → 閾値 0.80 (dim &gt; 2048) → 45% &lt; 0.80 → graph-first 維持
    /// </summary>
    [Fact]
    public void Threshold_scales_with_dim()
    {
        using (var tx = _db.BeginTransaction())
        {
            for (int i = 0; i < 45; i++) _ = tx.CreateNode("Doc");
            for (int i = 0; i < 55; i++) _ = tx.CreateNode("Other");
            tx.Commit();
        }

        var stats = _db.CollectStats();
        stats.HasFastLabelIndex.Should().BeTrue();

        var candidate = new ScanBuilder("Doc");

        var pkLow = new PendingKnnBuilder(
            candidate, IndexName, new float[] { 1, 0, 0, 0 }, k: 5, dim: 128);
        pkLow.Materialize(stats, _db.Schema)
            .Should().BeOfType<FilterBuilder>(
                "dim=128 (threshold 0.30) で sel=45% は fallback 発火");

        var pkHigh = new PendingKnnBuilder(
            candidate, IndexName, new float[] { 1, 0, 0, 0 }, k: 5, dim: 3072);
        pkHigh.Materialize(stats, _db.Schema)
            .Should().BeOfType<FilteredKnnNodeSourceBuilder>(
                "dim=3072 (threshold 0.80) で sel=45% は graph-first 維持");
    }

    private static PendingKnnBuilder ReadPending(GraphTraversal<NodeId> traversal)
    {
        var field = typeof(GraphTraversal<NodeId>).GetField(
            "_builder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (PendingKnnBuilder)field.GetValue(traversal)!;
    }
}
