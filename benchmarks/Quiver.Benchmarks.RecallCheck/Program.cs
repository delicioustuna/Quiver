using Quiver;
using Quiver.Core;
using Quiver.Testing;
using System.Diagnostics;

// true recall@10 の二段ゲート。
//   1) legacy ゲート — 旧既定 (M=16/Mmax0=32/efC=200) の比較基準を維持する。
//   2) default ゲート — recall@10 ≥ 0.95 を満たす新既定 (M=32/Mmax0=64/efC=400)
//      がその水準を維持し続けることを保証する。
// どちらも brute-force exact top-10 を ground truth とし、30% 削除後は生存集合で再計算する。

var scenarios = new[]
{
    new Scenario(
        Name: "legacy",
        HnswM: 16, HnswMMax0: 32, HnswEfConstruction: 200,
        MinimumRecall: 0.80),
    new Scenario(
        Name: "default",
        HnswM: 32, HnswMMax0: 64, HnswEfConstruction: 400,
        MinimumRecall: 0.95),
};

bool allPassed = true;
foreach (var scenario in scenarios)
    allPassed &= RunScenario(scenario);

if (!allPassed)
{
    Console.Error.WriteLine("RecallCheck FAILED: one or more recall gates fell below their threshold.");
    return 1;
}

Console.WriteLine("RecallCheck PASSED");
return 0;

static bool RunScenario(Scenario scenario)
{
    const string IndexName = "recall_check";
    string directory = Path.Combine(
        Path.GetTempPath(), "quiver_recall_check_" + Guid.NewGuid().ToString("N"));
    string path = Path.Combine(directory, "graph.quiver");

    try
    {
        var random = new Random(VectorRecallCorpus.Seed);
        var corpus = new List<float[]>(VectorRecallCorpus.RecallCount);
        var live = new bool[VectorRecallCorpus.RecallCount];
        Array.Fill(live, true);

        using var db = QuiverDatabase.Open(path);
        const string VectorProperty = "embedding";
        using (var schemaTx = db.BeginWriteTransaction())
        {
            schemaTx.EditSchema.CreateIndex(new VectorIndexDefinition(
                IndexName,
                new PropertyTarget(PropertyOwnerKind.Vertex, VectorProperty),
                VectorRecallCorpus.RecallDimensions,
                DistanceMetric.Cosine,
                HnswM: scenario.HnswM,
                HnswMMax0: scenario.HnswMMax0,
                HnswEfConstruction: scenario.HnswEfConstruction));
            schemaTx.Commit();
        }

        var buildStopwatch = Stopwatch.StartNew();
        using (var tx = db.BeginWriteTransaction())
        {
            for (int i = 0; i < VectorRecallCorpus.RecallCount; i++)
            {
                var vector = VectorRecallCorpus.NextVector(
                    random, VectorRecallCorpus.RecallDimensions);
                corpus.Add(vector);
                var vertex = tx.CreateVertex("Doc");
                tx.SetVectorProperty(EntityRef.From(vertex), VectorProperty, vector);
            }
            tx.Commit();
        }
        buildStopwatch.Stop();

        var queries = Enumerable.Range(0, VectorRecallCorpus.QueryCount)
            .Select(_ => VectorRecallCorpus.NextVector(
                random, VectorRecallCorpus.RecallDimensions))
            .ToArray();

        if (scenario.Name == "legacy")
            MeasureEfSearchSweep(db, IndexName, corpus, live, queries);

        var before = MeasureRecallAndLatency(db, IndexName, corpus, live, queries);
        Console.WriteLine(
            $"[{scenario.Name}] before_delete recall@{VectorRecallCorpus.K}={before.Recall:F3}, " +
            $"build_ms={buildStopwatch.Elapsed.TotalMilliseconds:F0}, " +
            $"search_mean_ms={before.MeanLatencyMs:F3} " +
            $"(N={VectorRecallCorpus.RecallCount}, dim={VectorRecallCorpus.RecallDimensions}, " +
            $"M={scenario.HnswM}, Mmax0={scenario.HnswMMax0}, efC={scenario.HnswEfConstruction})");

        var order = Enumerable.Range(0, VectorRecallCorpus.RecallCount).ToArray();
        new Random(VectorRecallCorpus.Seed ^ 0x5EED).Shuffle(order);
        using (var tx = db.BeginWriteTransaction())
        {
            int deleteCount = VectorRecallCorpus.RecallCount * 3 / 10;
            for (int i = 0; i < deleteCount; i++)
            {
                int seq = order[i];
                tx.RemoveProperty(new VertexId(seq), VectorProperty);
                live[seq] = false;
            }
            tx.Commit();
        }

        double after = MeasureRecall(db, IndexName, corpus, live, queries);
        Console.WriteLine(
            $"[{scenario.Name}] after_delete_30pct recall@{VectorRecallCorpus.K}={after:F3}");

        bool passed = before.Recall >= scenario.MinimumRecall && after >= scenario.MinimumRecall;
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
    QuiverDatabase db,
    string indexName,
    IReadOnlyList<float[]> corpus,
    IReadOnlyList<bool> live,
    IReadOnlyList<float[]> queries,
    VectorSearchOptions? options = null)
    => MeasureRecallAndLatency(db, indexName, corpus, live, queries, options).Recall;

static (double Recall, double MeanLatencyMs) MeasureRecallAndLatency(
    QuiverDatabase db,
    string indexName,
    IReadOnlyList<float[]> corpus,
    IReadOnlyList<bool> live,
    IReadOnlyList<float[]> queries,
    VectorSearchOptions? options = null)
{
    using var warmupTx = db.BeginReadTransaction();
    using (var warmup = warmupTx.KnnSearch(
        indexName, queries[0], VectorRecallCorpus.K, options))
        while (warmup.MoveNext()) { }

    double total = 0;
    long elapsedTicks = 0;
    foreach (var query in queries)
    {
        var approximate = new List<long>(VectorRecallCorpus.K);
        long started = Stopwatch.GetTimestamp();
        using var read = db.BeginReadTransaction();
        using (var cursor = read.KnnSearch(
            indexName, query, VectorRecallCorpus.K, options))
            while (cursor.MoveNext()) approximate.Add(cursor.Current.Owner.Sequence);
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
    QuiverDatabase db,
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
