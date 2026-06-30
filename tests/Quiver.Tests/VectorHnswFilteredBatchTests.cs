using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// HNSW を使う <c>KnnSearchFiltered</c> と <c>KnnSearchBatch</c> を検証する。
/// バッチ検索は各クエリを独立に探索し、単一の KnnSearch と一致することを確認する。
/// 低選択率のフィルター付き検索が HNSW 探索後のフィルターで動作し、
/// 全走査と比較して高い再現率を保つことも確認する。
/// </summary>
public sealed class VectorHnswFilteredBatchTests : IDisposable
{
    private const string IndexName = "embed";
    private readonly string _dir;
    private readonly string _path;

    public VectorHnswFilteredBatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_arch6g_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static float[] RandomVec(Random rng, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() * 2 - 1);
        return v;
    }

    private static List<long> TopK(VectorSearchCursor cur, int k)
    {
        using (cur)
        {
            var got = new List<long>();
            while (cur.MoveNext() && got.Count < k) got.Add(cur.Current.EntityId);
            return got;
        }
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0f : (float)(dot / denom);
    }

    [Fact]
    public void Batch_matches_per_query_hnsw_search()
    {
        const int Dim = 16, N = 500, K = 8, Q = 10;
        var rng = new Random(4242);

        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Cosine, "test", null));

        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                var n = tx.CreateNode("Doc");
                db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, RandomVec(rng, Dim));
            }
            tx.Commit();
        }

        var queries = new ReadOnlyMemory<float>[Q];
        for (int q = 0; q < Q; q++) queries[q] = RandomVec(rng, Dim);

        var batch = db.Vectors.KnnSearchBatch(IndexName, queries, K);
        for (int q = 0; q < Q; q++)
        {
            var fromBatch = TopK(batch[q], K);
            var fromSingle = TopK(db.Vectors.KnnSearch(IndexName, queries[q].Span, K), K);
            // 同一 HNSW を通るので batch と per-query は完全一致する。
            fromBatch.Should().Equal(fromSingle);
        }
    }

    [Fact]
    public void Filtered_low_selectivity_uses_hnsw_with_high_recall()
    {
        const int Dim = 32, N = 800, K = 10, Queries = 15;
        var rng = new Random(31415);
        var corpus = new List<float[]>(N);

        using var db = GraphDatabase.Open(_path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Node, db.Schema.GetOrCreatePropertyKey("t"),
            Dim, DistanceMetric.Cosine, "test", null));

        // 全ノードを同一ラベル "Doc" にして候補 = 全件 (低選択率 → HNSW filtered branch)。
        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                var v = RandomVec(rng, Dim);
                corpus.Add(v);
                var n = tx.CreateNode("Doc");
                db.Vectors.SetVector(EntityKind.Node, n.Value, IndexName, v);
            }
            tx.Commit();
        }

        double totalRecall = 0;
        using var rtx = db.BeginReadOnlyTransaction();
        var g = rtx.G(db.Schema);
        for (int qi = 0; qi < Queries; qi++)
        {
            var q = RandomVec(rng, Dim);
            // DSL の FilterByKnn は候補 = HasLabel("Doc") 全件に対する HNSW filtered 検索を通る。
            var filtered = g.Nodes().HasLabel("Doc")
                .FilterByKnn(IndexName, q, K)
                .ToList()
                .Select(n => n.Sequence)
                .ToList();

            var brute = Enumerable.Range(0, N)
                .Select(i => (Seq: (long)i, Score: Cosine(q, corpus[i])))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Seq)
                .Take(K).Select(x => x.Seq).ToList();

            int overlap = filtered.Count(x => brute.Contains(x));
            totalRecall += overlap / (double)K;
        }
        (totalRecall / Queries).Should().BeGreaterThanOrEqualTo(0.85);
    }
}
