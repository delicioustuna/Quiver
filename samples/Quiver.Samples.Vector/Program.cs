// Quiver.Samples.Vector — KNN を起点とするトラバーサルと graph-first ハイブリッド検索。
//
// 実行: dotnet run --project samples/Quiver.Samples.Vector

using Quiver;
using Quiver.Api;
using Quiver.Core;

string dir = Path.Combine(Path.GetTempPath(), "quiver_vec_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));

    // ── インデックス定義 ──
    const string indexName = "person_bio_v1";
    var bioKey = db.Schema.GetOrCreatePropertyKey("bio");
    db.Vectors.CreateVectorIndex(new VectorIndexSpec(
        Name: indexName,
        EntityKind: EntityKind.Vertex,
        SourcePropertyKeyId: bioKey,
        Dimensions: 4,
        Metric: DistanceMetric.Cosine,
        ProviderId: "sample-static"));

    VertexId aliceId, bobId, carolId;

    // ── データ投入 ──
    using (var tx = db.BeginTransaction())
    {
        var g = tx.G(db.Schema);
        aliceId = g.AddVertex("Person").P("name", "Alice").Next();
        bobId   = g.AddVertex("Person").P("name", "Bob").Next();
        carolId = g.AddVertex("Person").P("name", "Carol").Next();

        db.Vectors.SetVector(EntityKind.Vertex, aliceId.Value, indexName, new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        db.Vectors.SetVector(EntityKind.Vertex, bobId.Value,   indexName, new float[] { 0.0f, 0.1f, 0.2f, 0.5f });
        db.Vectors.SetVector(EntityKind.Vertex, carolId.Value, indexName, new float[] { 0.9f, 0.8f, 0.7f, 0.6f });
        tx.Commit();
    }

    // ── 1. KNN を起点とするトラバーサル ──
    Console.WriteLine("── 1. g.Knn(query, k=2) ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);
        var top2Names = g.Knn(indexName, new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, k: 2)
                         .Values("name")
                         .ToList();
        Console.WriteLine($"  上位 2 件: {string.Join(", ", top2Names)}");
    }

    // ── 2. グラフファーストな複合検索 (フィルタしてから KNN) ──
    Console.WriteLine();
    Console.WriteLine("── 2. graph-first hybrid ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);
        var filtered = g.Vertices().HasLabel("Person")
                        .FilterByKnn(indexName, new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, k: 1)
                        .Values("name")
                        .ToList();
        Console.WriteLine($"  graph-first 上位 1 件: {string.Join(", ", filtered)}");
    }

    // ── 3. 生スコア付きの直接 KNN ──
    Console.WriteLine();
    Console.WriteLine("── 3. 生スコア付き db.Vectors.KnnSearch ──");
    using (var cursor = db.Vectors.KnnSearch(indexName, new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, k: 3))
    {
        while (cursor.MoveNext())
        {
            var hit = cursor.Current;
            Console.WriteLine($"  {hit.EntityKind}#{hit.EntityId}  score={hit.Score:F4}");
        }
    }
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}
