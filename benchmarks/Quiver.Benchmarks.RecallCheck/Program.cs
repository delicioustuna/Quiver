using Quiver;
using Quiver.Core;
using Quiver.Testing;
using System.Diagnostics;

// VP-5: true recall@10 の二段ゲート。
//   1) default ゲート — 既定構築パラメタ (M=16/Mmax0=32/efC=200) の品質「劣化」を監視する。
//      既定構成の実測は ~0.825 であり 0.95 SLA には届かない (docs/spec/08_known_limits.md
//      #hnsw-default-recall)。閾値は実測から余裕をとった床値で、改善タスク (VP-2 実測後の
//      既定見直し等) の分母になる。
//   2) sla ゲート — recall@10 ≥ 0.95 を満たすと検証済みの高品質構成 (M=32/Mmax0=64/efC=400)
//      がその水準を維持し続けることを保証する。
// どちらも brute-force exact top-10 を ground truth とし、30% 削除後は生存集合で再計算する。

var scenarios = new[]
{
    new Scenario(
        Name: "default",
        HnswM: 16, HnswMMax0: 32, HnswEfConstruction: 200,
        MinimumRecall: 0.80),
    new Scenario(
        Name: "sla",
        HnswM: 32, HnswMMax0: 64, HnswEfConstruction: 400,
        MinimumRecall: 0.95),
};

bool allPassed = true;
foreach (var scenario in scenarios)
    allPassed &= RunScenario(scenario);

if (!allPassed)
{
    Console.Error.WriteLine("VP-5 FAILED: one or more recall gates fell below their threshold.");
    return 1;
}

Console.WriteLine("VP-5 PASSED");
return 0;

static bool RunScenario(Scenario scenario)
{
    const string IndexName = "vp5_recall";
    string directory = Path.Combine(
        Path.GetTempPath(), "quiver_vp5_recall_" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(directory, "graph.quiver");

    try
    {
        var random = new Random(VectorRecallCorpus.Seed);
        var corpus = new List<float[]>(VectorRecallCorpus.RecallCount);
        var live = new bool[VectorRecallCorpus.RecallCount];
        Array.Fill(live, true);

        using var db = GraphDatabase.Open(path);
        db.Vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName,
            EntityKind.Node,
            db.Schema.GetOrCreatePropertyKey("embedding"),
            VectorRecallCorpus.RecallDimensions,
            DistanceMetric.Cosine,
            "VP-5 deterministic corpus",
            HnswM: scenario.HnswM,
            HnswMMax0: scenario.HnswMMax0,
            HnswEfConstruction: scenario.HnswEfConstruction));

        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < VectorRecallCorpus.RecallCount; i++)
            {
                var vector = VectorRecallCorpus.NextVector(
                    random, VectorRecallCorpus.RecallDimensions);
                corpus.Add(vector);
                var node = tx.CreateNode("Doc");
                tx.SetVector(EntityKind.Node, node.Value, IndexName, vector);
            }
            tx.Commit();
        }

        var queries = Enumerable.Range(0, VectorRecallCorpus.QueryCount)
            .Select(_ => VectorRecallCorpus.NextVector(
                random, VectorRecallCorpus.RecallDimensions))
            .ToArray();

        if (scenario.Name == "default")
            MeasureEfSearchSweep(db, IndexName, corpus, live, queries);

        double before = MeasureRecall(db, IndexName, corpus, live, queries);
        Console.WriteLine(
            $"[{scenario.Name}] before_delete recall@{VectorRecallCorpus.K}={before:F3} " +
            $"(N={VectorRecallCorpus.RecallCount}, dim={VectorRecallCorpus.RecallDimensions}, " +
            $"M={scenario.HnswM}, Mmax0={scenario.HnswMMax0}, efC={scenario.HnswEfConstruction})");

        var order = Enumerable.Range(0, VectorRecallCorpus.RecallCount).ToArray();
        new Random(VectorRecallCorpus.Seed ^ 0x5EED).Shuffle(order);
        using (var tx = db.BeginTransaction())
        {
            int deleteCount = VectorRecallCorpus.RecallCount * 3 / 10;
            for (int i = 0; i < deleteCount; i++)
            {
                int seq = order[i];
                tx.RemoveVector(EntityKind.Node, seq, IndexName);
                live[seq] = false;
            }
            tx.Commit();
        }

        double after = MeasureRecall(db, IndexName, corpus, live, queries);
        Console.WriteLine(
            $"[{scenario.Name}] after_delete_30pct recall@{VectorRecallCorpus.K}={after:F3}");

        bool passed = before >= scenario.MinimumRecall && after >= scenario.MinimumRecall;
        if (!passed)
        {
            Console.Error.WriteLine(
                $"[{scenario.Name}] FAILED: recall must be >= {scenario.MinimumRecall:F2} " +
                "before and after deletion.");
        }
        return passed;
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static double MeasureRecall(
    GraphDatabase db,
    string indexName,
    IReadOnlyList<float[]> corpus,
    IReadOnlyList<bool> live,
    IReadOnlyList<float[]> queries,
    VectorSearchOptions? options = null)
    => MeasureRecallAndLatency(db, indexName, corpus, live, queries, options).Recall;

static (double Recall, double MeanLatencyMs) MeasureRecallAndLatency(
    GraphDatabase db,
    string indexName,
    IReadOnlyList<float[]> corpus,
    IReadOnlyList<bool> live,
    IReadOnlyList<float[]> queries,
    VectorSearchOptions? options = null)
{
    using (var warmup = db.Vectors.KnnSearch(
        indexName, queries[0], VectorRecallCorpus.K, options))
        while (warmup.MoveNext()) { }

    double total = 0;
    long elapsedTicks = 0;
    foreach (var query in queries)
    {
        var approximate = new List<long>(VectorRecallCorpus.K);
        long started = Stopwatch.GetTimestamp();
        using (var cursor = db.Vectors.KnnSearch(
            indexName, query, VectorRecallCorpus.K, options))
            while (cursor.MoveNext()) approximate.Add(cursor.Current.EntityId);
        elapsedTicks += Stopwatch.GetTimestamp() - started;

        var exact = Enumerable.Range(0, corpus.Count)
            .Where(i => live[i])
            .Select(i => (Seq: (long)i, Score: Cosine(query, corpus[i])))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Seq)
            .Take(VectorRecallCorpus.K)
            .Select(x => x.Seq)
            .ToHashSet();
        total += approximate.Count(exact.Contains) / (double)VectorRecallCorpus.K;
    }
    return (
        total / queries.Count,
        elapsedTicks * 1000.0 / Stopwatch.Frequency / queries.Count);
}

static void MeasureEfSearchSweep(
    GraphDatabase db,
    string indexName,
    IReadOnlyList<float[]> corpus,
    IReadOnlyList<bool> live,
    IReadOnlyList<float[]> queries)
{
    Console.WriteLine("efSearch, recall@10, mean_latency_ms");
    foreach (int efSearch in new[] { 32, 64, 100, 200 })
    {
        var options = new VectorSearchOptions { EfSearch = efSearch };
        var result = MeasureRecallAndLatency(db, indexName, corpus, live, queries, options);
        Console.WriteLine(
            $"{efSearch}, {result.Recall:F3}, {result.MeanLatencyMs:F3}");
    }
}

static float Cosine(float[] left, float[] right)
{
    double dot = 0;
    double leftNorm = 0;
    double rightNorm = 0;
    for (int i = 0; i < left.Length; i++)
    {
        dot += left[i] * right[i];
        leftNorm += left[i] * left[i];
        rightNorm += right[i] * right[i];
    }
    double denominator = Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm);
    return denominator == 0 ? 0 : (float)(dot / denominator);
}

internal sealed record Scenario(
    string Name,
    int HnswM,
    int HnswMMax0,
    int HnswEfConstruction,
    double MinimumRecall);
