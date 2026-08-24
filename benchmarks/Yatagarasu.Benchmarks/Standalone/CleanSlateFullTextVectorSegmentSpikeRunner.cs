using System.Diagnostics;
using System.Runtime.InteropServices;
using Yatagarasu.Core;
using Yatagarasu.Testing;

namespace Yatagarasu.Benchmarks.Standalone;

/// <summary>
/// Standalone prototype for the clean-slate full-text/vector segment contract.
/// It measures segment fan-out and top-k merge without changing the product storage format.
/// </summary>
public static class CleanSlateFullTextVectorSegmentSpikeRunner
{
    private const int DefaultFullTextChunks = 100_000;
    private const int DefaultFullTextQueries = 500;
    private const int DefaultSegmentCount = 4;
    private const int TopK = 20;
    private const double RequiredFullTextP50Ms = 8.55;
    private const double BaselineFullTextWalAmplification = 11.74;
    private const double RequiredVectorRecall = 0.950;
    private const double K1 = 1.2;
    private const double B = 0.75;

    public static int Run(IReadOnlyList<string> args)
    {
        int fullTextChunks = Parse(args, 0, DefaultFullTextChunks);
        int fullTextQueries = Parse(args, 1, DefaultFullTextQueries);
        int vectorCount = Parse(args, 2, VectorRecallCorpus.RecallCount);
        int vectorQueries = Parse(args, 3, VectorRecallCorpus.QueryCount);
        int segmentCount = Math.Clamp(Parse(args, 4, DefaultSegmentCount), 1, 32);

        Console.WriteLine("=== Clean-slate full-text/vector segment spike ===");
        Console.WriteLine(
            $"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine(
            $"fullTextChunks={fullTextChunks}, fullTextQueries={fullTextQueries}, " +
            $"vectorCount={vectorCount}, vectorQueries={vectorQueries}, segments={segmentCount}");
        Console.WriteLine();

        bool contractPass = RunContractValidation();
        Console.WriteLine();
        var fullText = MeasureFullTextSegments(fullTextChunks, fullTextQueries, segmentCount);
        Console.WriteLine();
        var vector = MeasureVectorSegments(vectorCount, vectorQueries, segmentCount);

        bool pass =
            contractPass &&
            fullText.P50Ms <= RequiredFullTextP50Ms &&
            fullText.RecallPass &&
            fullText.MergePass &&
            fullText.WriteAmplification <= BaselineFullTextWalAmplification &&
            vector.Recall >= RequiredVectorRecall &&
            vector.MergePass;

        Console.WriteLine();
        Console.WriteLine(
            $"segment_spike_result, result={(pass ? "PASS" : "FAIL")}, " +
            $"fulltext_p50_ms={fullText.P50Ms:F3}, " +
            $"write_amplification={fullText.WriteAmplification:F2}, " +
            $"vector_recall={vector.Recall:F3}");
        return pass ? 0 : 2;
    }

    private static int Parse(IReadOnlyList<string> args, int index, int fallback)
        => index < args.Count && int.TryParse(args[index], out int value) && value > 0
            ? value
            : fallback;

    private static FullTextResult MeasureFullTextSegments(
        int chunkCount,
        int queryCount,
        int segmentCount)
    {
        Console.WriteLine("--- full-text segment prototype ---");
        var vocab = new ZipfVocabulary(Math.Clamp(chunkCount / 2, 4_000, 60_000));
        var segments = Enumerable.Range(0, segmentCount)
            .Select(i => new TextSegment(i))
            .ToArray();

        var rng = new Random(11);
        long rawBytes = 0;
        for (int doc = 0; doc < chunkCount; doc++)
        {
            var terms = MakeChunkTerms(vocab, rng);
            rawBytes += EstimateRawBytes(terms);
            int segment = (int)((long)doc * segmentCount / chunkCount);
            segments[segment].Add(doc, terms);
        }

        var catalog = new TextSegmentCatalog(segments);
        var queries = MakeQueries(vocab, queryCount, seed: 99);

        for (int i = 0; i < Math.Min(20, queries.Length); i++)
            _ = catalog.Search(queries[i], TopK);

        var latencies = new double[queries.Length];
        bool recallPass = true;
        for (int i = 0; i < queries.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            var segmented = catalog.Search(queries[i], TopK);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;

            var strict = catalog.SearchStrict(queries[i], TopK);
            recallPass &= SameResults(segmented, strict);
        }

        Array.Sort(latencies);
        long initialSegmentBytes = segments.Sum(s => s.EstimatedBytes);
        var merged = TextSegment.Merge(0, segments);
        var mergedCatalog = new TextSegmentCatalog([merged]);
        bool mergePass = true;
        foreach (var query in queries.Take(Math.Min(100, queries.Length)))
            mergePass &= SameResults(catalog.Search(query, TopK), mergedCatalog.Search(query, TopK));

        long totalWriteBytes = initialSegmentBytes + merged.EstimatedBytes;
        double amplification = rawBytes > 0 ? totalWriteBytes / (double)rawBytes : double.NaN;
        double p50 = Percentile(latencies, 0.50);
        double p95 = Percentile(latencies, 0.95);

        bool searchPass = p50 <= RequiredFullTextP50Ms;
        bool ampPass = amplification <= BaselineFullTextWalAmplification;
        Console.WriteLine(
            $"fulltext_segments, chunks={chunkCount}, queries={queryCount}, segments={segmentCount}, " +
            $"p50_ms={p50:F3}, p95_ms={p95:F3}, required_p50_ms={RequiredFullTextP50Ms:F2}, " +
            $"topk_recall={(recallPass ? "PASS" : "FAIL")}, merge_equivalence={(mergePass ? "PASS" : "FAIL")}, " +
            $"search_result={(searchPass ? "PASS" : "FAIL")}");
        Console.WriteLine(
            $"fulltext_segment_write_amp, raw_bytes={rawBytes}, initial_segment_bytes={initialSegmentBytes}, " +
            $"merge_output_bytes={merged.EstimatedBytes}, total_write_bytes={totalWriteBytes}, " +
            $"amplification={amplification:F2}, required_max={BaselineFullTextWalAmplification:F2}, " +
            $"result={(ampPass ? "PASS" : "FAIL")}");
        Console.WriteLine(
            "csv,segment_fulltext,chunks,queries,segments,p50_ms,p95_ms,write_amplification,topk_recall,merge_equivalence,result");
        Console.WriteLine(
            $"csv,segment_fulltext,{chunkCount},{queryCount},{segmentCount},{p50:F3},{p95:F3}," +
            $"{amplification:F2},{(recallPass ? "PASS" : "FAIL")},{(mergePass ? "PASS" : "FAIL")}," +
            $"{(searchPass && ampPass && recallPass && mergePass ? "PASS" : "FAIL")}");

        return new FullTextResult(p50, p95, amplification, recallPass, mergePass);
    }

    private static bool RunContractValidation()
    {
        Console.WriteLine("--- segment contract validation ---");
        bool textPass = ValidateTextContract();
        bool vectorPass = ValidateVectorContract();
        bool hybridPass = ValidateHybridSnapshotContract();
        bool pass = textPass && vectorPass && hybridPass;
        Console.WriteLine(
            $"segment_contract, text={(textPass ? "PASS" : "FAIL")}, " +
            $"vector={(vectorPass ? "PASS" : "FAIL")}, " +
            $"hybrid={(hybridPass ? "PASS" : "FAIL")}, result={(pass ? "PASS" : "FAIL")}");
        Console.WriteLine("csv,segment_contract,text,vector,hybrid,result");
        Console.WriteLine(
            $"csv,segment_contract,{(textPass ? "PASS" : "FAIL")}," +
            $"{(vectorPass ? "PASS" : "FAIL")},{(hybridPass ? "PASS" : "FAIL")}," +
            $"{(pass ? "PASS" : "FAIL")}");
        return pass;
    }

    private static bool ValidateTextContract()
    {
        var baseSegment = new TextSegment(0);
        baseSegment.AddVersion(1, 1, ["alpha", "common"]);
        baseSegment.AddVersion(2, 1, ["beta", "common"]);
        var oldSnapshot = new TextSegmentCatalog([baseSegment]);

        var deltaSegment = new TextSegment(1);
        deltaSegment.AddVersion(1, 2, ["gamma", "common"]);
        deltaSegment.Delete(2, 2);
        var currentSnapshot = new TextSegmentCatalog([baseSegment, deltaSegment]);
        var mergedSnapshot = new TextSegmentCatalog([TextSegment.Merge(0, [baseSegment, deltaSegment])]);

        bool oldSeesOldDocument = oldSnapshot.Search(["alpha"], TopK).Any(x => x.EntityId == 1);
        bool currentHidesOldVersion = currentSnapshot.Search(["alpha"], TopK).All(x => x.EntityId != 1);
        bool currentSeesNewVersion = currentSnapshot.Search(["gamma"], TopK).Any(x => x.EntityId == 1);
        bool currentHidesDeletedDocument = currentSnapshot.Search(["beta"], TopK).All(x => x.EntityId != 2);
        bool mergeEquivalent =
            SameResults(currentSnapshot.Search(["gamma", "common"], TopK), mergedSnapshot.Search(["gamma", "common"], TopK)) &&
            SameResults(currentSnapshot.Search(["beta", "common"], TopK), mergedSnapshot.Search(["beta", "common"], TopK));

        return oldSeesOldDocument &&
               currentHidesOldVersion &&
               currentSeesNewVersion &&
               currentHidesDeletedDocument &&
               mergeEquivalent;
    }

    private static bool ValidateVectorContract()
    {
        var baseSegment = new VectorSegment(0, 2);
        baseSegment.AddVersion(1, 1, [1f, 0f]);
        baseSegment.AddVersion(2, 1, [0f, 1f]);
        var oldSnapshot = new VectorSegmentCatalog([baseSegment]);

        var deltaSegment = new VectorSegment(1, 2);
        deltaSegment.AddVersion(1, 2, [0f, 1f]);
        deltaSegment.Delete(2, 2);
        var currentSnapshot = new VectorSegmentCatalog([baseSegment, deltaSegment]);
        var mergedSnapshot = new VectorSegmentCatalog([VectorSegment.Merge(0, [baseSegment, deltaSegment])]);

        bool oldSeesOldVector = oldSnapshot.Search([1f, 0f], 2).Any(x => x.EntityId == 1);
        bool currentSeesUpdatedVector = currentSnapshot.Search([0f, 1f], 2).FirstOrDefault().EntityId == 1;
        bool currentHidesDeletedVector = currentSnapshot.Search([0f, 1f], 2).All(x => x.EntityId != 2);
        bool mergeEquivalent =
            SameResults(currentSnapshot.Search([0f, 1f], 2), mergedSnapshot.Search([0f, 1f], 2)) &&
            SameResults(currentSnapshot.Search([1f, 0f], 2), mergedSnapshot.Search([1f, 0f], 2));

        return oldSeesOldVector &&
               currentSeesUpdatedVector &&
               currentHidesDeletedVector &&
               mergeEquivalent;
    }

    private static bool ValidateHybridSnapshotContract()
    {
        var textBase = new TextSegment(0);
        textBase.AddVersion(1, 1, ["alpha"]);
        var vectorBase = new VectorSegment(0, 2);
        vectorBase.AddVersion(1, 1, [1f, 0f]);

        var textDelta = new TextSegment(1);
        textDelta.AddVersion(1, 2, ["gamma"]);
        var vectorDelta = new VectorSegment(1, 2);
        vectorDelta.AddVersion(1, 2, [0f, 1f]);

        var oldText = new TextSegmentCatalog([textBase]);
        var oldVector = new VectorSegmentCatalog([vectorBase]);
        var currentText = new TextSegmentCatalog([textBase, textDelta]);
        var currentVector = new VectorSegmentCatalog([vectorBase, vectorDelta]);

        bool oldSnapshotMatches = HybridSearch(oldText, 1, ["alpha"], oldVector, 1, [1f, 0f], 10)
            .Any(x => x == 1);
        bool currentSnapshotMatches = HybridSearch(currentText, 2, ["gamma"], currentVector, 2, [0f, 1f], 10)
            .Any(x => x == 1);

        bool rejectedMixedSnapshot = false;
        try
        {
            _ = HybridSearch(oldText, 1, ["alpha"], currentVector, 2, [0f, 1f], 10);
        }
        catch (InvalidOperationException)
        {
            rejectedMixedSnapshot = true;
        }

        return oldSnapshotMatches && currentSnapshotMatches && rejectedMixedSnapshot;
    }

    private static long[] HybridSearch(
        TextSegmentCatalog text,
        int textGeneration,
        IReadOnlyList<string> queryTerms,
        VectorSegmentCatalog vector,
        int vectorGeneration,
        float[] queryVector,
        int k)
    {
        if (textGeneration != vectorGeneration)
            throw new InvalidOperationException("Hybrid search requires text and vector catalogs from the same snapshot.");

        var scores = new Dictionary<long, double>();
        var textHits = text.Search(queryTerms, k);
        for (int rank = 0; rank < textHits.Length; rank++)
            CollectionsMarshal.GetValueRefOrAddDefault(scores, textHits[rank].EntityId, out _) += 1.0 / (60 + rank + 1);

        var vectorHits = vector.Search(queryVector, k);
        for (int rank = 0; rank < vectorHits.Length; rank++)
            CollectionsMarshal.GetValueRefOrAddDefault(scores, vectorHits[rank].EntityId, out _) += 1.0 / (60 + rank + 1);

        return scores
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Take(k)
            .Select(x => x.Key)
            .ToArray();
    }

    private static VectorResult MeasureVectorSegments(
        int vectorCount,
        int queryCount,
        int segmentCount)
    {
        Console.WriteLine("--- vector segment prototype ---");
        var random = new Random(VectorRecallCorpus.Seed);
        var segments = Enumerable.Range(0, segmentCount)
            .Select(i => new VectorSegment(i, VectorRecallCorpus.RecallDimensions))
            .ToArray();

        for (int i = 0; i < vectorCount; i++)
        {
            var vector = VectorRecallCorpus.NextVector(random, VectorRecallCorpus.RecallDimensions);
            int segment = (int)((long)i * segmentCount / vectorCount);
            segments[segment].Add(i, vector);
        }

        var catalog = new VectorSegmentCatalog(segments);
        var queries = Enumerable.Range(0, queryCount)
            .Select(_ => VectorRecallCorpus.NextVector(random, VectorRecallCorpus.RecallDimensions))
            .ToArray();

        _ = catalog.Search(queries[0], VectorRecallCorpus.K);
        double recallTotal = 0;
        var latencies = new double[queries.Length];
        for (int i = 0; i < queries.Length; i++)
        {
            var query = queries[i];
            var sw = Stopwatch.StartNew();
            var segmented = catalog.Search(query, VectorRecallCorpus.K);
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;

            var strict = catalog.SearchStrict(query, VectorRecallCorpus.K);
            recallTotal += Recall(segmented, strict);
        }

        Array.Sort(latencies);
        var merged = VectorSegment.Merge(0, segments);
        var mergedCatalog = new VectorSegmentCatalog([merged]);
        bool mergePass = true;
        foreach (var query in queries)
            mergePass &= SameResults(catalog.Search(query, VectorRecallCorpus.K), mergedCatalog.Search(query, VectorRecallCorpus.K));

        double recall = recallTotal / queries.Length;
        double p50 = Percentile(latencies, 0.50);
        double p95 = Percentile(latencies, 0.95);
        bool recallPass = recall >= RequiredVectorRecall;
        Console.WriteLine(
            $"vector_segments, vectors={vectorCount}, queries={queryCount}, segments={segmentCount}, " +
            $"recall@{VectorRecallCorpus.K}={recall:F3}, required_recall={RequiredVectorRecall:F3}, " +
            $"p50_ms={p50:F3}, p95_ms={p95:F3}, merge_equivalence={(mergePass ? "PASS" : "FAIL")}, " +
            $"result={(recallPass && mergePass ? "PASS" : "FAIL")}");
        Console.WriteLine("csv,segment_vector,vectors,queries,segments,recall,p50_ms,p95_ms,merge_equivalence,result");
        Console.WriteLine(
            $"csv,segment_vector,{vectorCount},{queryCount},{segmentCount},{recall:F3},{p50:F3},{p95:F3}," +
            $"{(mergePass ? "PASS" : "FAIL")},{(recallPass && mergePass ? "PASS" : "FAIL")}");

        return new VectorResult(recall, p50, p95, mergePass);
    }

    private static bool SameResults(IReadOnlyList<SearchHit> left, IReadOnlyList<SearchHit> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].EntityId != right[i].EntityId)
                return false;
        }
        return true;
    }

    private static double Recall(IReadOnlyList<VectorHit> approximate, IReadOnlyList<VectorHit> exact)
    {
        var exactIds = exact.Select(x => x.EntityId).ToHashSet();
        return approximate.Count(x => exactIds.Contains(x.EntityId)) / (double)exact.Count;
    }

    private static bool SameResults(IReadOnlyList<VectorHit> left, IReadOnlyList<VectorHit> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].EntityId != right[i].EntityId)
                return false;
        }
        return true;
    }

    private static string[] MakeChunkTerms(ZipfVocabulary vocab, Random rng)
    {
        int len = rng.Next(60, 120);
        var terms = new string[len];
        for (int i = 0; i < terms.Length; i++)
            terms[i] = vocab.Sample(rng);
        return terms;
    }

    private static string[][] MakeQueries(
        ZipfVocabulary vocab,
        int queryCount,
        int seed)
    {
        var rng = new Random(seed);
        var queries = new string[queryCount][];
        for (int i = 0; i < queries.Length; i++)
        {
            int terms = rng.Next(2, 6);
            queries[i] = Enumerable.Range(0, terms)
                .Select(_ => vocab.Sample(rng))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        return queries;
    }

    private static long EstimateRawBytes(IReadOnlyList<string> terms)
    {
        long bytes = Math.Max(0, terms.Count - 1);
        foreach (string term in terms)
            bytes += term.Length;
        return bytes;
    }

    private static double Percentile(double[] sorted, double p)
    {
        int index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private readonly record struct FullTextResult(
        double P50Ms,
        double P95Ms,
        double WriteAmplification,
        bool RecallPass,
        bool MergePass);

    private readonly record struct VectorResult(
        double Recall,
        double P50Ms,
        double P95Ms,
        bool MergePass);

    private readonly record struct SearchHit(int EntityId, double Score);

    private sealed class TextSegment(int id)
    {
        private readonly Dictionary<string, Dictionary<long, int>> _postings = new(StringComparer.Ordinal);
        private readonly Dictionary<long, int> _docLengths = new();
        private readonly Dictionary<int, int> _tombstones = new();

        public int Id { get; } = id;
        public int DocumentCount => _docLengths.Count;
        public long TotalDocumentLength { get; private set; }
        public long EstimatedBytes { get; private set; }

        public IEnumerable<KeyValuePair<long, int>> Entries => _docLengths;
        public IEnumerable<KeyValuePair<int, int>> Tombstones => _tombstones;

        public void Add(int docId, IReadOnlyList<string> terms) => AddVersion(docId, 1, terms);

        public void AddVersion(int docId, int generation, IReadOnlyList<string> terms)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string term in terms)
                CollectionsMarshal.GetValueRefOrAddDefault(counts, term, out _)++;

            long entryKey = EntryKey(docId, generation);
            _docLengths[entryKey] = terms.Count;
            TotalDocumentLength += terms.Count;
            EstimatedBytes += 8;
            foreach (var (term, tf) in counts)
            {
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(_postings, term, out bool exists);
                if (!exists)
                {
                    list = new Dictionary<long, int>();
                    EstimatedBytes += term.Length + 16;
                }
                list![entryKey] = tf;
                EstimatedBytes += 12;
            }
        }

        public void Delete(int docId, int generation)
        {
            _tombstones[docId] = Math.Max(
                generation,
                _tombstones.TryGetValue(docId, out int existing) ? existing : 0);
            EstimatedBytes += 16;
        }

        public IReadOnlyDictionary<long, int>? TryGetPostings(string term)
            => _postings.TryGetValue(term, out var list) ? list : null;

        public int DocumentLength(long entryKey) => _docLengths[entryKey];

        public IEnumerable<string> Terms => _postings.Keys;

        public static long EntryKey(int docId, int generation)
            => ((long)docId << 32) | (uint)generation;

        public static int DocId(long entryKey) => (int)(entryKey >> 32);

        public static int Generation(long entryKey) => (int)entryKey;

        public static TextSegment Merge(int id, IReadOnlyList<TextSegment> inputs)
        {
            var merged = new TextSegment(id);
            foreach (var input in inputs)
            {
                foreach (var (entryKey, length) in input._docLengths)
                {
                    merged._docLengths[entryKey] = length;
                    merged.TotalDocumentLength += length;
                    merged.EstimatedBytes += 8;
                }

                foreach (var (term, postings) in input._postings)
                {
                    ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(merged._postings, term, out bool exists);
                    if (!exists)
                    {
                        list = new Dictionary<long, int>();
                        merged.EstimatedBytes += term.Length + 16;
                    }

                    foreach (var (entryKey, tf) in postings)
                    {
                        list![entryKey] = tf;
                        merged.EstimatedBytes += 12;
                    }
                }

                foreach (var (docId, generation) in input._tombstones)
                    merged.Delete(docId, generation);
            }
            return merged;
        }
    }

    private sealed class TextSegmentCatalog
    {
        private readonly TextSegment[] _segments;
        private readonly Dictionary<int, int> _latestGeneration = new();
        private readonly HashSet<int> _deleted = new();
        private readonly int _documentCount;
        private readonly double _averageDocumentLength;
        private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);

        public TextSegmentCatalog(TextSegment[] segments)
        {
            _segments = segments;
            foreach (var segment in segments)
            {
                foreach (var (entryKey, _) in segment.Entries)
                {
                    int docId = TextSegment.DocId(entryKey);
                    int generation = TextSegment.Generation(entryKey);
                    if (!_latestGeneration.TryGetValue(docId, out int current) || generation >= current)
                    {
                        _latestGeneration[docId] = generation;
                        _deleted.Remove(docId);
                    }
                }

                foreach (var (docId, generation) in segment.Tombstones)
                {
                    if (!_latestGeneration.TryGetValue(docId, out int current) || generation >= current)
                    {
                        _latestGeneration[docId] = generation;
                        _deleted.Add(docId);
                    }
                }
            }

            _documentCount = 0;
            long totalLength = 0;
            foreach (var segment in segments)
            {
                foreach (var (entryKey, length) in segment.Entries)
                {
                    if (!IsLive(entryKey))
                        continue;
                    _documentCount++;
                    totalLength += length;
                }
            }
            _averageDocumentLength = _documentCount > 0 ? totalLength / (double)_documentCount : 1.0;

            foreach (var segment in segments)
            {
                foreach (string term in segment.Terms)
                {
                    int df = segment.TryGetPostings(term)?.Keys.Count(IsLive) ?? 0;
                    CollectionsMarshal.GetValueRefOrAddDefault(_documentFrequency, term, out _) += df;
                }
            }
        }

        public SearchHit[] Search(IReadOnlyList<string> queryTerms, int k)
        {
            var candidates = new List<SearchHit>(k * _segments.Length);
            foreach (var segment in _segments)
                candidates.AddRange(SearchSegment(segment, queryTerms, k));
            return candidates
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.EntityId)
                .Take(k)
                .ToArray();
        }

        public SearchHit[] SearchStrict(IReadOnlyList<string> queryTerms, int k)
        {
            var scores = new Dictionary<int, double>();
            foreach (var segment in _segments)
                ScoreInto(segment, queryTerms, scores);
            return Sort(scores, k);
        }

        private SearchHit[] SearchSegment(TextSegment segment, IReadOnlyList<string> queryTerms, int k)
        {
            var scores = new Dictionary<int, double>();
            ScoreInto(segment, queryTerms, scores);
            return Sort(scores, k);
        }

        private void ScoreInto(
            TextSegment segment,
            IReadOnlyList<string> queryTerms,
            Dictionary<int, double> scores)
        {
            foreach (string term in queryTerms)
            {
                if (!_documentFrequency.TryGetValue(term, out int df) || df == 0)
                    continue;
                var postings = segment.TryGetPostings(term);
                if (postings is null)
                    continue;

                double idf = Math.Log(1.0 + (_documentCount - df + 0.5) / (df + 0.5));
                foreach (var (entryKey, tf) in postings)
                {
                    if (!IsLive(entryKey))
                        continue;
                    int docId = TextSegment.DocId(entryKey);
                    int dl = segment.DocumentLength(entryKey);
                    double denom = tf + K1 * (1.0 - B + B * dl / _averageDocumentLength);
                    double score = idf * (tf * (K1 + 1.0)) / denom;
                    CollectionsMarshal.GetValueRefOrAddDefault(scores, docId, out _) += score;
                }
            }
        }

        private static SearchHit[] Sort(Dictionary<int, double> scores, int k)
            => scores
                .Select(x => new SearchHit(x.Key, x.Value))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.EntityId)
                .Take(k)
                .ToArray();

        private bool IsLive(long entryKey)
        {
            int docId = TextSegment.DocId(entryKey);
            int generation = TextSegment.Generation(entryKey);
            return !_deleted.Contains(docId) &&
                   _latestGeneration.TryGetValue(docId, out int current) &&
                   current == generation;
        }
    }

    private readonly record struct VectorHit(long EntityId, float Score);

    private sealed class VectorSegment(int id, int dimensions)
    {
        private readonly List<VectorEntry> _entries = new();

        public int Id { get; } = id;
        public int Dimensions { get; } = dimensions;
        public IReadOnlyList<VectorEntry> Entries => _entries;

        public void Add(long entityId, float[] vector) => AddVersion(entityId, 1, vector);

        public void AddVersion(long entityId, int generation, float[] vector)
            => _entries.Add(new VectorEntry(entityId, generation, Deleted: false, vector));

        public void Delete(long entityId, int generation)
            => _entries.Add(new VectorEntry(entityId, generation, Deleted: true, Vector: []));

        public static VectorSegment Merge(int id, IReadOnlyList<VectorSegment> inputs)
        {
            var merged = new VectorSegment(id, inputs[0].Dimensions);
            foreach (var input in inputs)
            {
                foreach (var item in input._entries)
                    merged._entries.Add(item);
            }
            return merged;
        }
    }

    private readonly record struct VectorEntry(
        long EntityId,
        int Generation,
        bool Deleted,
        float[] Vector);

    private sealed class VectorSegmentCatalog(VectorSegment[] segments)
    {
        private readonly Dictionary<long, int> _latestGeneration = BuildLatestGeneration(segments);
        private readonly HashSet<long> _deleted = BuildDeleted(segments);

        public VectorHit[] Search(float[] query, int k)
        {
            var candidates = new List<VectorHit>(k * segments.Length);
            foreach (var segment in segments)
                candidates.AddRange(SearchSegment(segment, query, k));
            return Sort(candidates, k);
        }

        public VectorHit[] SearchStrict(float[] query, int k)
        {
            var candidates = new List<VectorHit>();
            foreach (var segment in segments)
            {
                foreach (var entry in segment.Entries)
                {
                    if (IsLive(entry))
                        candidates.Add(new VectorHit(entry.EntityId, Cosine(query, entry.Vector)));
                }
            }
            return Sort(candidates, k);
        }

        private VectorHit[] SearchSegment(VectorSegment segment, float[] query, int k)
        {
            var hits = new List<VectorHit>(segment.Entries.Count);
            foreach (var entry in segment.Entries)
            {
                if (IsLive(entry))
                    hits.Add(new VectorHit(entry.EntityId, Cosine(query, entry.Vector)));
            }
            return Sort(hits, k);
        }

        private static VectorHit[] Sort(List<VectorHit> hits, int k)
            => hits
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.EntityId)
                .Take(k)
                .ToArray();

        private static float Cosine(float[] left, float[] right)
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

        private bool IsLive(VectorEntry entry)
            => !entry.Deleted &&
               !_deleted.Contains(entry.EntityId) &&
               _latestGeneration.TryGetValue(entry.EntityId, out int generation) &&
               generation == entry.Generation;

        private static Dictionary<long, int> BuildLatestGeneration(VectorSegment[] segments)
        {
            var latest = new Dictionary<long, int>();
            foreach (var segment in segments)
            {
                foreach (var entry in segment.Entries)
                {
                    if (!latest.TryGetValue(entry.EntityId, out int current) || entry.Generation >= current)
                        latest[entry.EntityId] = entry.Generation;
                }
            }
            return latest;
        }

        private static HashSet<long> BuildDeleted(VectorSegment[] segments)
        {
            var latest = BuildLatestGeneration(segments);
            var deleted = new HashSet<long>();
            foreach (var segment in segments)
            {
                foreach (var entry in segment.Entries)
                {
                    if (entry.Deleted &&
                        latest.TryGetValue(entry.EntityId, out int generation) &&
                        generation == entry.Generation)
                    {
                        deleted.Add(entry.EntityId);
                    }
                }
            }
            return deleted;
        }
    }

    private sealed class ZipfVocabulary
    {
        private readonly string[] _terms;
        private readonly double[] _cdf;

        public ZipfVocabulary(int size)
        {
            _terms = new string[size];
            _cdf = new double[size];
            double sum = 0;
            for (int i = 0; i < size; i++)
            {
                _terms[i] = "term" + i.ToString("D5");
                sum += 1.0 / (i + 1);
                _cdf[i] = sum;
            }

            for (int i = 0; i < size; i++)
                _cdf[i] /= sum;
        }

        public string Sample(Random rng)
        {
            double value = rng.NextDouble();
            int lo = 0;
            int hi = _cdf.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_cdf[mid] < value)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return _terms[lo];
        }
    }
}
