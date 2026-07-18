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
    using (var schemaTx = db.BeginWriteTransaction())
    {
        schemaTx.EditSchema.GetOrCreatePropertyKey("bio");
        schemaTx.EditSchema.CreateIndex(new VectorIndexDefinition(
            indexName,
            new PropertyTarget(PropertyOwnerKind.Vertex, "bio", "Person"),
            Dimensions: 4,
            Metric: DistanceMetric.Cosine));
        schemaTx.Commit();
    }

    VertexId aliceId, bobId, carolId;

    // ── データ投入 ──
    using (var tx = db.BeginWriteTransaction())
    {
        var g = tx.Query;
        aliceId = tx.Mutate.AddVertex("Person").P("name", "Alice").Next();
        bobId   = tx.Mutate.AddVertex("Person").P("name", "Bob").Next();
        carolId = tx.Mutate.AddVertex("Person").P("name", "Carol").Next();

        tx.SetVectorProperty(EntityRef.From(aliceId), "bio", [0.1f, 0.2f, 0.3f, 0.4f]);
        tx.SetVectorProperty(EntityRef.From(bobId), "bio", [0.0f, 0.1f, 0.2f, 0.5f]);
        tx.SetVectorProperty(EntityRef.From(carolId), "bio", [0.9f, 0.8f, 0.7f, 0.6f]);
        tx.Commit();
    }

    // ── 1. KNN を起点とするトラバーサル ──
    Console.WriteLine("── 1. g.Knn(query, k=2) ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var top2Names = g.Knn(indexName, new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, k: 2)
                         .Values("name")
                         .ToList();
        Console.WriteLine($"  上位 2 件: {string.Join(", ", top2Names)}");
    }

    // ── 2. グラフファーストな複合検索 (フィルタしてから KNN) ──
    Console.WriteLine();
    Console.WriteLine("── 2. graph-first hybrid ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var filtered = g.Vertices().HasLabel("Person")
                        .FilterByKnn(indexName, new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, k: 1)
                        .Values("name")
                        .ToList();
        Console.WriteLine($"  graph-first 上位 1 件: {string.Join(", ", filtered)}");
    }

    // ── 3. 生スコア付きの直接 KNN ──
    Console.WriteLine();
    Console.WriteLine("── 3. 生スコア付き transaction KNN ──");
    using (var tx = db.BeginReadTransaction())
    {
        using var cursor = tx.KnnSearch(indexName, [0.1f, 0.2f, 0.3f, 0.4f], k: 3);
        while (cursor.MoveNext())
        {
            var hit = cursor.Current;
            Console.WriteLine($"  {hit.Owner.Kind}#{hit.Owner.Sequence}  score={hit.Score:F4}");
        }
    }
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}
