using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Text;

internal static class RetrievalQualityGate
{
    private const string TextIndex = "retrieval_text";
    private const string VectorIndex = "retrieval_vector";
    private const string TenantIndex = "retrieval_tenant";
    private const string BodyProperty = "body";
    private const string VectorProperty = "embedding";
    private const string TenantProperty = "tenant";
    private const int Dimensions = 48;
    private const int K = 10;
    private const int DocumentsPerQuery = 5;
    private const int DocumentCount = 1_000;

    internal static bool Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "yatagarasu_retrieval_quality_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        using var releaseBuild = new ManualResetEventSlim();
        using var buildsStarted = new CountdownEvent(2);
        int vectorBuildSignaled = 0;
        int textBuildSignaled = 0;

        BinaryGraphStorageBackend.VectorSegmentBuildStartedForTest = () =>
        {
            if (Interlocked.Exchange(ref vectorBuildSignaled, 1) == 0)
                buildsStarted.Signal();
            releaseBuild.Wait(TimeSpan.FromSeconds(30));
        };
        BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = () =>
        {
            if (Interlocked.Exchange(ref textBuildSignaled, 1) == 0)
                buildsStarted.Signal();
            releaseBuild.Wait(TimeSpan.FromSeconds(30));
        };

        try
        {
            var queries = CreateQueries();
            using var db = YatagarasuDatabase.Open(path);
            CreateIndexes(db);
            SeedCorpus(db, queries);

            if (!buildsStarted.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException(
                    "Retrieval quality fixture did not start both segment merges.");

            QualitySnapshot commitLocal = Measure(db, queries, metadataCandidates: false);
            Print("commit_local_many_segments", commitLocal);

            releaseBuild.Set();
            var backend = (BinaryGraphStorageBackend)db.BackendInternal;
            backend.WaitForVectorSegmentMergeForTest();
            backend.WaitForFullTextSegmentMergeForTest();
            if (backend.VectorSegmentMergeErrorForTest is { } vectorError)
                throw new InvalidOperationException("Vector segment merge failed.", vectorError);
            if (backend.FullTextSegmentMergeErrorForTest is { } textError)
                throw new InvalidOperationException("Full-text segment merge failed.", textError);

            QualitySnapshot merged = Measure(db, queries, metadataCandidates: false);
            Print("merged", merged);

            MutateCorpus(db, queries);
            backend.WaitForVectorSegmentMergeForTest();
            backend.WaitForFullTextSegmentMergeForTest();
            QualitySnapshot mutated = Measure(db, queries, metadataCandidates: false);
            Print("after_update_delete", mutated);

            QualitySnapshot metadata = Measure(db, queries, metadataCandidates: true);
            Print("metadata_candidates", metadata);

            bool passed =
                Passes(commitLocal)
                && Passes(merged)
                && Passes(mutated)
                && Passes(metadata);
            if (!passed)
            {
                Console.Error.WriteLine(
                    "RetrievalQuality FAILED: Recall@10 >= 0.95, MRR >= 0.90, "
                    + "and NDCG@10 >= 0.90 are required for BM25, vector, and RRF.");
            }
            return passed;
        }
        finally
        {
            releaseBuild.Set();
            BinaryGraphStorageBackend.VectorSegmentBuildStartedForTest = null;
            BinaryGraphStorageBackend.FullTextSegmentBuildStartedForTest = null;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void CreateIndexes(YatagarasuDatabase db)
    {
        using var schema = db.BeginWriteTransaction();
        schema.EditSchema.CreateIndex(new FullTextIndexDefinition(
            TextIndex,
            new PropertyTarget(PropertyOwnerKind.Vertex, BodyProperty, "Chunk"),
            Filters: [new JapaneseOrthographicVariantFilter()],
            SegmentPolicy: new FullTextSegmentPolicy(
                MaximumDeltaEntries: 75,
                MaximumSegments: 2)));
        schema.EditSchema.CreateIndex(new VectorIndexDefinition(
            VectorIndex,
            new PropertyTarget(PropertyOwnerKind.Vertex, VectorProperty, "Chunk"),
            Dimensions,
            DistanceMetric.Cosine,
            SegmentPolicy: new VectorSegmentPolicy(
                MaximumDeltaEntries: 75,
                MaximumSegments: 2)));
        schema.EditSchema.CreateIndex(new ScalarIndexDefinition(
            TenantIndex,
            new PropertyTarget(PropertyOwnerKind.Vertex, TenantProperty, "Chunk"),
            IndexKind.StringEquality));
        schema.Commit();
    }

    private static void SeedCorpus(
        YatagarasuDatabase db,
        IReadOnlyList<QualityQuery> queries)
    {
        var random = new Random(0x51A7);
        var drafts = new List<DocumentDraft>(DocumentCount);
        for (int topic = 0; topic < queries.Count; topic++)
        {
            QualityQuery query = queries[topic];
            for (int relevant = 0; relevant < DocumentsPerQuery; relevant++)
            {
                int grade = relevant == 0 ? 3 : relevant <= 2 ? 2 : 1;
                string repetitions = string.Join(
                    ' ',
                    Enumerable.Repeat(query.IndexedTerm, grade));
                drafts.Add(new(
                    $"{repetitions}。{query.Description}。根拠文書 {relevant}。",
                    CreateRelevantVector(topic, relevant),
                    query.Tenant,
                    topic,
                    grade));
            }
        }

        while (drafts.Count < DocumentCount)
        {
            int id = drafts.Count;
            drafts.Add(new(
                $"background filler artifact {id} neutral archival material",
                CreateNoiseVector(random),
                id % 2 == 0 ? "public" : "private",
                Topic: -1,
                Grade: 0));
        }

        const int batchSize = 100;
        for (int offset = 0; offset < drafts.Count; offset += batchSize)
        {
            using var write = db.BeginWriteTransaction();
            foreach (DocumentDraft draft in drafts.Skip(offset).Take(batchSize))
            {
                VertexId document = write.CreateVertex("Chunk");
                write.SetProperty(
                    document,
                    BodyProperty,
                    PropertyValue.FromString(draft.Body));
                write.SetProperty(
                    document,
                    TenantProperty,
                    PropertyValue.FromString(draft.Tenant));
                write.SetVectorProperty(
                    EntityRef.From(document),
                    VectorProperty,
                    draft.Vector);
                if (draft.Topic >= 0)
                    queries[draft.Topic].Relevant[document.Value] = draft.Grade;
            }
            write.Commit();
        }
    }

    private static void MutateCorpus(
        YatagarasuDatabase db,
        IReadOnlyList<QualityQuery> queries)
    {
        var random = new Random(0xD311);
        using var write = db.BeginWriteTransaction();
        foreach (QualityQuery query in queries)
        {
            long[] lowestGrades = query.Relevant
                .OrderBy(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .Take(2)
                .Select(static pair => pair.Key)
                .ToArray();

            write.DeleteVertex(new VertexId(lowestGrades[0]));
            write.SetProperty(
                new VertexId(lowestGrades[1]),
                BodyProperty,
                PropertyValue.FromString("retired background material"));
            write.SetVectorProperty(
                EntityRef.From(new VertexId(lowestGrades[1])),
                VectorProperty,
                CreateNoiseVector(random));
            query.Relevant.Remove(lowestGrades[0]);
            query.Relevant.Remove(lowestGrades[1]);
        }
        write.Commit();
    }

    private static QualitySnapshot Measure(
        YatagarasuDatabase db,
        IReadOnlyList<QualityQuery> queries,
        bool metadataCandidates)
    {
        var textRankings = new List<IReadOnlyList<long>>(queries.Count);
        var vectorRankings = new List<IReadOnlyList<long>>(queries.Count);
        var fusedRankings = new List<IReadOnlyList<long>>(queries.Count);

        foreach (QualityQuery query in queries)
        {
            using var read = db.BeginReadTransaction();
            List<long> text;
            List<long> vector;
            if (metadataCandidates)
            {
                text = EntityValues(read.Query.Vertices()
                    .Has(TenantProperty, query.Tenant)
                    .FilterByText(TextIndex, query.Text, K)
                    .ToList());
                vector = EntityValues(read.Query.Vertices()
                    .Has(TenantProperty, query.Tenant)
                    .FilterByKnn(VectorIndex, query.Vector, K)
                    .ToList());
            }
            else
            {
                text = EntityValues(
                    read.Query.Search(TextIndex, query.Text, K).ToList());
                vector = [];
                using VectorSearchCursor cursor = read.KnnSearch(
                    VectorIndex,
                    query.Vector,
                    K);
                while (cursor.MoveNext())
                    vector.Add(cursor.Current.Owner.Value);
            }

            IReadOnlyList<IReadOnlyList<long>> channels = [text, vector];
            List<long> fused = ReciprocalRankFusion.Fuse(channels, K)
                .Select(static result => result.EntityId)
                .ToList();
            textRankings.Add(text);
            vectorRankings.Add(vector);
            fusedRankings.Add(fused);
        }

        return new(
            Evaluate(queries, textRankings),
            Evaluate(queries, vectorRankings),
            Evaluate(queries, fusedRankings));
    }

    private static List<long> EntityValues(IEnumerable<VertexId> entities)
        => entities.Select(static id => id.Value).ToList();

    private static QualityMetrics Evaluate(
        IReadOnlyList<QualityQuery> queries,
        IReadOnlyList<IReadOnlyList<long>> rankings)
    {
        double recall = 0;
        double reciprocalRank = 0;
        double ndcg = 0;
        for (int queryIndex = 0; queryIndex < queries.Count; queryIndex++)
        {
            IReadOnlyDictionary<long, int> relevant = queries[queryIndex].Relevant;
            IReadOnlyList<long> ranking = rankings[queryIndex];
            int found = 0;
            double dcg = 0;
            for (int rank = 0; rank < Math.Min(K, ranking.Count); rank++)
            {
                if (!relevant.TryGetValue(ranking[rank], out int grade))
                    continue;
                found++;
                dcg += (Math.Pow(2, grade) - 1) / Math.Log2(rank + 2);
            }

            recall += found / (double)relevant.Count;
            int firstRelevant = ranking
                .Take(K)
                .Select((id, rank) => (id, rank))
                .Where(item => relevant.ContainsKey(item.id))
                .Select(static item => item.rank)
                .DefaultIfEmpty(-1)
                .First();
            if (firstRelevant >= 0)
                reciprocalRank += 1.0 / (firstRelevant + 1);

            double ideal = relevant.Values
                .OrderByDescending(static grade => grade)
                .Take(K)
                .Select((grade, rank) =>
                    (Math.Pow(2, grade) - 1) / Math.Log2(rank + 2))
                .Sum();
            ndcg += ideal > 0 ? dcg / ideal : 0;
        }

        return new(
            recall / queries.Count,
            reciprocalRank / queries.Count,
            ndcg / queries.Count);
    }

    private static bool Passes(QualitySnapshot snapshot)
        => Passes(snapshot.Bm25)
            && Passes(snapshot.Vector)
            && Passes(snapshot.Rrf);

    private static bool Passes(QualityMetrics metrics)
        => metrics.RecallAt10 >= 0.95
            && metrics.Mrr >= 0.90
            && metrics.NdcgAt10 >= 0.90;

    private static void Print(string phase, QualitySnapshot snapshot)
    {
        Console.WriteLine(
            $"[retrieval:{phase}] "
            + $"BM25 recall@10={snapshot.Bm25.RecallAt10:F3} mrr={snapshot.Bm25.Mrr:F3} ndcg@10={snapshot.Bm25.NdcgAt10:F3}; "
            + $"vector recall@10={snapshot.Vector.RecallAt10:F3} mrr={snapshot.Vector.Mrr:F3} ndcg@10={snapshot.Vector.NdcgAt10:F3}; "
            + $"RRF recall@10={snapshot.Rrf.RecallAt10:F3} mrr={snapshot.Rrf.Mrr:F3} ndcg@10={snapshot.Rrf.NdcgAt10:F3}");
    }

    private static List<QualityQuery> CreateQueries()
    {
        (string Text, string Indexed, string Description)[] definitions =
        [
            ("渡辺", "渡邉", "日本語の異字体を明示的に検索する"),
            ("猫", "猫", "一文字の日本語クエリを検索する"),
            ("ｸﾞﾗﾌ検索", "グラフ検索", "半角カタカナを含む検索を正規化する"),
            ("全文索引", "全文索引", "日本語BM25の索引品質を確認する"),
            ("障害復旧", "障害復旧", "クラッシュ後の復旧手順を説明する"),
            ("文書分割", "文書分割", "長い文書をチャンクへ分割する"),
            ("近傍探索", "近傍探索", "ベクトルの近傍を探索する"),
            ("権限制御", "権限制御", "文書アクセス権を絞り込む"),
            ("更新削除", "更新削除", "索引更新と削除を検証する"),
            ("東京都", "東京都", "CJK bigramの境界を検証する"),
            ("orionledger", "orionledger", "English identifier retrieval"),
            ("saffroncache", "saffroncache", "English cache documentation"),
            ("meadowindex", "meadowindex", "English index documentation"),
            ("harborwriter", "harborwriter", "single writer guidance"),
            ("opalreader", "opalreader", "snapshot reader guidance"),
            ("willowvacuum", "willowvacuum", "vacuum maintenance guidance"),
            ("cobaltsegment", "cobaltsegment", "segment lifecycle guidance"),
            ("embermetric", "embermetric", "retrieval metric guidance"),
            ("juniperfilter", "juniperfilter", "metadata filter guidance"),
            ("lunarprofile", "lunarprofile", "ingestion profile guidance"),
            ("CancellationToken", "CancellationToken", "C sharp cancellation code"),
            ("ReadOnlySpan", "ReadOnlySpan", "C sharp span code"),
            ("InvalidOperationException", "InvalidOperationException", "C sharp exception code"),
            ("ConfigureAwait", "ConfigureAwait", "asynchronous C sharp code"),
            ("IAsyncEnumerable", "IAsyncEnumerable", "streaming C sharp code"),
            ("ValueTask", "ValueTask", "allocation aware C sharp code"),
            ("stackalloc", "stackalloc", "stack allocation code"),
            ("record struct", "record struct", "value record code"),
            ("where T class", "where T class", "generic constraint code"),
            ("TryGetValue", "TryGetValue", "dictionary lookup code"),
            ("local retrieval database", "local retrieval database", "long English RAG guidance"),
            ("hybrid rank fusion", "hybrid rank fusion", "long hybrid search guidance"),
            ("exact nearest neighbors", "exact nearest neighbors", "long vector oracle guidance"),
            ("immutable segment publication", "immutable segment publication", "long segment publication guidance"),
            ("metadata candidate ranking", "metadata candidate ranking", "long filtered ranking guidance"),
            ("short query quality", "short query quality", "short and long text comparison"),
            ("deterministic tie breaking", "deterministic tie breaking", "stable ordering guidance"),
            ("document length normalization", "document length normalization", "BM25 norm guidance"),
            ("日本語 English mixed", "日本語 English mixed", "mixed language retrieval guidance"),
            ("code 検索 quality", "code 検索 quality", "mixed code retrieval guidance"),
        ];

        return definitions
            .Select((definition, topic) => new QualityQuery(
                definition.Text,
                definition.Indexed,
                definition.Description,
                topic % 2 == 0 ? "public" : "private",
                CreateQueryVector(topic)))
            .ToList();
    }

    private static float[] CreateQueryVector(int topic)
    {
        var vector = new float[Dimensions];
        vector[topic] = 1;
        return vector;
    }

    private static float[] CreateRelevantVector(int topic, int relevant)
    {
        float noise = relevant switch
        {
            0 => 0,
            <= 2 => 0.01f,
            _ => 0.02f,
        };
        var vector = CreateQueryVector(topic);
        vector[40 + relevant] = noise;
        return vector;
    }

    private static float[] CreateNoiseVector(Random random)
    {
        var vector = new float[Dimensions];
        for (int i = 0; i < vector.Length; i++)
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        return vector;
    }

    private sealed class QualityQuery(
        string text,
        string indexedTerm,
        string description,
        string tenant,
        float[] vector)
    {
        public string Text { get; } = text;
        public string IndexedTerm { get; } = indexedTerm;
        public string Description { get; } = description;
        public string Tenant { get; } = tenant;
        public float[] Vector { get; } = vector;
        public Dictionary<long, int> Relevant { get; } = [];
    }

    private sealed record DocumentDraft(
        string Body,
        float[] Vector,
        string Tenant,
        int Topic,
        int Grade);

    private readonly record struct QualityMetrics(
        double RecallAt10,
        double Mrr,
        double NdcgAt10);

    private readonly record struct QualitySnapshot(
        QualityMetrics Bm25,
        QualityMetrics Vector,
        QualityMetrics Rrf);
}
